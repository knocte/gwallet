#!/usr/bin/env fsharpi

open System
open System.IO
open System.Linq
open System.Diagnostics

open System.Text
open System.Text.RegularExpressions
#r "System.Core.dll"
#r "System.Xml.Linq.dll"
open System.Xml.Linq

#r "System.Configuration"
open System.Configuration
#load "InfraLib/Misc.fs"
#load "InfraLib/Process.fs"
#load "InfraLib/Git.fs"
open FSX.Infrastructure
open Process

let UNIX_NAME = "gwallet"
let DEFAULT_FRONTEND = "GWallet.Frontend.Console"
let BACKEND = "GWallet.Backend"
let TEST_TYPE_UNIT = "Unit"
let TEST_TYPE_END2END = "End2End"

type BinaryConfig =
    | Debug
    | Release
    override self.ToString() =
        sprintf "%A" self

module MapHelper =
    let GetKeysOfMap (map: Map<'K,'V>): seq<'K> =
        map |> Map.toSeq |> Seq.map fst

    let MergeIntoMap<'K,'V when 'K: comparison> (from: seq<'K*'V>): Map<'K,seq<'V>> =
        let keys = from.Select (fun (k, v) -> k)
        let keyValuePairs =
            seq {
                for key in keys do
                    let valsForKey = (from.Where (fun (k, v) -> key = k)).Select (fun (k, v) -> v) |> seq
                    yield key,valsForKey
            }
        keyValuePairs |> Map.ofSeq

[<StructuralEquality;StructuralComparison>]
type private PackageInfo =
    { PackageId: string; PackageVersion: string }

type private Project =
    { FolderName: string }

[<CustomComparison; CustomEquality>]
type private ComparableFileInfo =
    { File: FileInfo }
    interface IComparable with
        member this.CompareTo obj =
            match obj with
            | null -> this.File.FullName.CompareTo null
            | :? ComparableFileInfo as other -> this.File.FullName.CompareTo other.File.FullName
            | _ -> invalidArg "obj" "not a ComparableFileInfo"
    override this.Equals obj =
        match obj with
        | :? ComparableFileInfo as other ->
            this.File.FullName.Equals(other.File.FullName)
        | _ -> false
    override this.GetHashCode () =
        this.File.FullName.GetHashCode()

let rec private GatherTarget (args: string list, targetSet: Option<string>): Option<string> =
    match args with
    | [] -> targetSet
    | head::tail ->
        if (targetSet.IsSome) then
            failwith "only one target can be passed to make"
        GatherTarget (tail, Some (head))

let scriptsDir = __SOURCE_DIRECTORY__ |> DirectoryInfo
let rootDir = Path.Combine(scriptsDir.FullName, "..") |> DirectoryInfo

let buildConfigFileName = "build.config"
let buildConfigContents =
    let buildConfig = FileInfo (Path.Combine (scriptsDir.FullName, buildConfigFileName))
    if not (buildConfig.Exists) then
        let configureLaunch =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows -> ".\\configure.bat"
            | _ -> "./configure.sh"
        Console.Error.WriteLine (sprintf "ERROR: configure hasn't been run yet, run %s first"
                                         configureLaunch)
        Environment.Exit 1

    let skipBlankLines line = not <| String.IsNullOrWhiteSpace line
    let splitLineIntoKeyValueTuple (line:string) =
        let pair = line.Split([|'='|], StringSplitOptions.RemoveEmptyEntries)
        if pair.Length <> 2 then
            failwithf "All lines in %s must conform to format:\n\tkey=value"
                      buildConfigFileName
        pair.[0], pair.[1]

    let buildConfigContents =
        File.ReadAllLines buildConfig.FullName
        |> Array.filter skipBlankLines
        |> Array.map splitLineIntoKeyValueTuple
        |> Map.ofArray
    buildConfigContents

let GetOrExplain key map =
    match map |> Map.tryFind key with
    | Some k -> k
    | None   -> failwithf "No entry exists in %s with a key '%s'."
                          buildConfigFileName key

let prefix = buildConfigContents |> GetOrExplain "Prefix"
let libPrefixDir = DirectoryInfo (Path.Combine (prefix, "lib", UNIX_NAME))
let binPrefixDir = DirectoryInfo (Path.Combine (prefix, "bin"))

let launcherScriptFile = Path.Combine (scriptsDir.FullName, "bin", UNIX_NAME) |> FileInfo
let mainBinariesDir binaryConfig = DirectoryInfo (Path.Combine(rootDir.FullName,
                                                               "src",
                                                               DEFAULT_FRONTEND,
                                                               "bin",
                                                               binaryConfig.ToString()))

let wrapperScript = """#!/usr/bin/env bash
set -eo pipefail

if [[ $SNAP ]]; then
    PKG_DIR=$SNAP/usr
    export MONO_PATH=$PKG_DIR/lib/mono/4.5
    export MONO_CONFIG=$SNAP/etc/mono/config
    export MONO_CFG_DIR=$SNAP/etc
    export MONO_REGISTRY_PATH=~/.mono/registry
    export MONO_GAC_PREFIX=$PKG_DIR/lib/mono/gac/
fi

DIR_OF_THIS_SCRIPT=$(dirname "$(realpath "$0")")
FRONTEND_PATH="$DIR_OF_THIS_SCRIPT/../lib/$UNIX_NAME/$GWALLET_PROJECT.exe"
exec mono "$FRONTEND_PATH" "$@"
"""

let nugetExe = Path.Combine(rootDir.FullName, ".nuget", "nuget.exe") |> FileInfo
let nugetPackagesSubDirName = "packages"

let PrintNugetVersion () =
    if not (nugetExe.Exists) then
        false
    else
        let nugetCmd =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows ->
                { Command = nugetExe.FullName; Arguments = String.Empty }
            | _ -> { Command = "mono"; Arguments = nugetExe.FullName }
        let nugetProc = Process.Execute (nugetCmd, Echo.Off)
        Console.WriteLine nugetProc.Output.StdOut
        if nugetProc.ExitCode = 0 then
            true
        else
            Console.Error.WriteLine nugetProc.Output.StdErr
            Console.WriteLine()
            failwith "nuget process' output contained errors ^"

let JustBuild binaryConfig maybeConstant =
    let buildTool = Map.tryFind "BuildTool" buildConfigContents
    if buildTool.IsNone then
        failwith "A BuildTool should have been chosen by the configure script, please report this bug"

    Console.WriteLine (sprintf "Building in %s mode..." (binaryConfig.ToString()))
    let configOption = sprintf "/p:Configuration=%s" (binaryConfig.ToString())
    let defineConstantsFromBuildConfig =
        match buildConfigContents |> Map.tryFind "DefineConstants" with
        | Some constants -> constants.Split([|";"|], StringSplitOptions.RemoveEmptyEntries) |> Seq.ofArray
        | None -> Seq.empty
    let allDefineConstants =
        match maybeConstant with
        | Some constant -> Seq.append [constant] defineConstantsFromBuildConfig
        | None -> defineConstantsFromBuildConfig
    let configOptions =
        if allDefineConstants.Any() then
            // FIXME: we shouldn't override the project's DefineConstants, but rather set "ExtraDefineConstants"
            // from the command line, and merge them later in the project file: see https://stackoverflow.com/a/32326853/544947
            sprintf "%s;DefineConstants=%s" configOption (String.Join(";", allDefineConstants))
        else
            configOption
    let buildProcess = Process.Execute ({ Command = buildTool.Value; Arguments = configOptions }, Echo.All)
    if (buildProcess.ExitCode <> 0) then
        Console.Error.WriteLine (sprintf "%s build failed" buildTool.Value)
        PrintNugetVersion() |> ignore
        Environment.Exit 1

    Directory.CreateDirectory(launcherScriptFile.Directory.FullName) |> ignore
    let wrapperScriptWithPaths =
        wrapperScript.Replace("$UNIX_NAME", UNIX_NAME)
                     .Replace("$GWALLET_PROJECT", DEFAULT_FRONTEND)
    File.WriteAllText (launcherScriptFile.FullName, wrapperScriptWithPaths)

let MakeCheckCommand (commandName: string) =
    if not (Process.CommandWorksInShell commandName) then
        Console.Error.WriteLine (sprintf "%s not found, please install it first" commandName)
        Environment.Exit 1

let GetPathToFrontendBinariesDir (binaryConfig: BinaryConfig) =
    Path.Combine (rootDir.FullName, "src", DEFAULT_FRONTEND, "bin", binaryConfig.ToString())

let GetPathToBackend () =
    Path.Combine (rootDir.FullName, "src", BACKEND)

let MakeAll maybeConstant =
    let buildConfig = BinaryConfig.Debug
    JustBuild buildConfig maybeConstant
    buildConfig

let RunFrontend (buildConfig: BinaryConfig) (maybeArgs: Option<string>) =
    let monoVersion = Map.tryFind "MonoPkgConfigVersion" buildConfigContents

    let pathToFrontend = Path.Combine(GetPathToFrontendBinariesDir buildConfig, DEFAULT_FRONTEND + ".exe")

    let fileName, finalArgs =
        match maybeArgs with
        | None | Some "" -> pathToFrontend,String.Empty
        | Some args -> pathToFrontend,args

    let startInfo = ProcessStartInfo(FileName = fileName, Arguments = finalArgs, UseShellExecute = false)
    startInfo.EnvironmentVariables.["MONO_ENV_OPTIONS"] <- "--debug"

    let proc = Process.Start startInfo
    proc.WaitForExit()
    proc

let RunTests (suite: string) =
    let findTestAssembly theSuite =
        let testAssemblyName = sprintf "GWallet.Backend.Tests.%s" theSuite
        let testAssembly = Path.Combine(rootDir.FullName, "src", testAssemblyName, "bin",
                                        testAssemblyName + ".dll") |> FileInfo
        if not testAssembly.Exists then
            failwithf "File not found: %s" testAssembly.FullName
        testAssembly

    // string*string means flag*value, e.g. "include" * "G2GFunder"
    let nunitCommandFor (testAssembly: FileInfo) (maybeArgs: Option<List<string*string>>): ProcessDetails =
        let convertArgsToString (charPrefixForFlag: char) =
            match maybeArgs with
            | None -> String.Empty
            | Some args ->
                sprintf "%s "
                    (String.Join (" ",
                                  args.Select(fun (flag,value) ->
                                    sprintf "%s%s %s" (charPrefixForFlag.ToString()) flag value
                                  )
                                 )
                    )

        match Misc.GuessPlatform() with
        | Misc.Platform.Linux ->
            let nunitCommand = "nunit-console"
            MakeCheckCommand nunitCommand

            let maybeExtraArgs = convertArgsToString '-'

            {
                Command = nunitCommand
                Arguments = sprintf "%s%s" maybeExtraArgs testAssembly.FullName
            }

        | _ ->
            let nunitVersion = "2.7.1"
            if not nugetExe.Exists then
                MakeAll None |> ignore

            let runnerExe =
                Path.Combine (
                    nugetPackagesSubDirName,
                    sprintf "NUnit.Runners.%s" nunitVersion,
                    "tools",
                    "nunit-console.exe"
                ) |> FileInfo

            if not runnerExe.Exists then
                let nugetInstallCommand =
                    {
                        Command = nugetExe.FullName
                        Arguments = sprintf "install NUnit.Runners -Version %s -OutputDirectory %s"
                                            nunitVersion nugetPackagesSubDirName
                    }
                Process.SafeExecute(nugetInstallCommand, Echo.All)
                    |> ignore

            let maybeExtraArgs = convertArgsToString '/'

            {
                Command = runnerExe.FullName
                Arguments = sprintf "%s%s" maybeExtraArgs testAssembly.FullName
            }

    let twoProcessTestNames =
        [
            "ChannelOpening"
            "MonoHopUnidirectionalPayments"
            "ChannelClosingAfterJustOpening"
            "ChannelClosingAfterSendingMonoHopPayments"
            "ChannelLocalForceClosing"
            "ChannelRemoteForceClosingByFunder"
            "ChannelRemoteForceClosingByFundee"
        ]

    let geewallet2geewalletTestNamePrefix, funderSuffix, fundeeSuffix = "G2G_", "_Funder", "_Fundee"

    let runTwoProcessTest testAssembly testName =
        let funderRunnerCommand =
            nunitCommandFor testAssembly (
                Some [("include", geewallet2geewalletTestNamePrefix + testName + funderSuffix)]
            )

        let fundeeRunnerCommand =
            nunitCommandFor testAssembly (
                Some [("include", geewallet2geewalletTestNamePrefix + testName + fundeeSuffix)]
            )

        let funderRun = async {
            let res = Process.Execute(funderRunnerCommand, Echo.All)
            if res.ExitCode <> 0 then
                Console.Error.WriteLine (testName + "Funder test failed")
                Environment.Exit 1
        }

        let fundeeRun = async {
            let res = Process.Execute(fundeeRunnerCommand, Echo.All)
            if res.ExitCode <> 0 then
                Console.Error.WriteLine (testName + "Fundee test failed")
                Environment.Exit 1
        }

        [funderRun; fundeeRun]
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

    let ourWalletToOurWalletEnd2EndTests () =
        let testAssembly = findTestAssembly TEST_TYPE_END2END

        for testName in twoProcessTestNames do
            runTwoProcessTest testAssembly testName

    Console.WriteLine (sprintf "Running %s tests..." suite)
    Console.WriteLine ()

    // so that we get file names in stack traces
    Environment.SetEnvironmentVariable("MONO_ENV_OPTIONS", "--debug")

    let testAssembly = findTestAssembly suite

    let allTwoProcessTestNamesToExclude =
        twoProcessTestNames
        |> Seq.ofList
        |> Seq.map
            (
                fun testName ->
                    [
                        geewallet2geewalletTestNamePrefix + testName + funderSuffix
                        geewallet2geewalletTestNamePrefix + testName + fundeeSuffix
                    ]
            )
        |> Seq.concat
        |> String.concat ","

    let runnerCommand =
        let maybeExcludeArgument =
            if suite = TEST_TYPE_END2END then
                Some [ ("exclude", allTwoProcessTestNamesToExclude) ]
            else
                None
        nunitCommandFor testAssembly maybeExcludeArgument

    let nunitRun = Process.Execute(runnerCommand,
                                   Echo.All)
    if (nunitRun.ExitCode <> 0) then
        Console.Error.WriteLine "Tests failed"
        Environment.Exit 1

    if suite = TEST_TYPE_END2END then
        Console.WriteLine "First end2end tests finished running, now about to launch geewallet2geewallet ones..."
        ourWalletToOurWalletEnd2EndTests ()

let maybeTarget = GatherTarget (Misc.FsxArguments(), None)
match maybeTarget with
| None ->
    MakeAll None |> ignore

| Some("release") ->
    JustBuild BinaryConfig.Release None

| Some "nuget" ->
    Console.WriteLine "This target is for debugging purposes."

    if not (PrintNugetVersion()) then
        Console.Error.WriteLine "Nuget executable has not been downloaded yet, try `make` alone first"
        Environment.Exit 1

| Some("zip") ->
    let zipCommand = "zip"
    MakeCheckCommand zipCommand

    let version = Misc.GetCurrentVersion(rootDir).ToString()

    let release = BinaryConfig.Release
    JustBuild release None
    let binDir = "bin"
    Directory.CreateDirectory(binDir) |> ignore

    let zipNameWithoutExtension = sprintf "%s.v.%s" UNIX_NAME version
    let zipName = sprintf "%s.zip" zipNameWithoutExtension
    let pathToZip = Path.Combine(binDir, zipName)
    if (File.Exists (pathToZip)) then
        File.Delete (pathToZip)

    let pathToFolderToBeZipped = Path.Combine(binDir, zipNameWithoutExtension)
    if (Directory.Exists (pathToFolderToBeZipped)) then
        Directory.Delete (pathToFolderToBeZipped, true)

    let pathToFrontend = GetPathToFrontendBinariesDir release
    let zipRun = Process.Execute({ Command = "cp"
                                   Arguments = sprintf "-rfvp %s %s" pathToFrontend pathToFolderToBeZipped },
                                 Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "Precopy for ZIP compression failed"
        Environment.Exit 1

    let previousCurrentDir = Directory.GetCurrentDirectory()
    Directory.SetCurrentDirectory binDir
    let zipLaunch = { Command = zipCommand
                      Arguments = sprintf "%s -r %s %s"
                                      zipCommand zipName zipNameWithoutExtension }
    let zipRun = Process.Execute(zipLaunch, Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "ZIP compression failed"
        Environment.Exit 1
    Directory.SetCurrentDirectory previousCurrentDir

| Some("check") ->
    RunTests TEST_TYPE_UNIT

| Some "check-end2end" ->
    RunTests "End2End"

| Some("install") ->
    let buildConfig = BinaryConfig.Release
    JustBuild buildConfig None

    let destDirUpperCase = Environment.GetEnvironmentVariable "DESTDIR"
    let destDirLowerCase = Environment.GetEnvironmentVariable "DestDir"
    let destDir =
        if not (String.IsNullOrEmpty destDirUpperCase) then
            destDirUpperCase |> DirectoryInfo
        elif not (String.IsNullOrEmpty destDirLowerCase) then
            destDirLowerCase |> DirectoryInfo
        else
            prefix |> DirectoryInfo

    let libDestDir = Path.Combine(destDir.FullName, "lib", UNIX_NAME) |> DirectoryInfo
    let binDestDir = Path.Combine(destDir.FullName, "bin") |> DirectoryInfo

    Console.WriteLine "Installing..."
    Console.WriteLine ()
    Misc.CopyDirectoryRecursively (mainBinariesDir buildConfig, libDestDir, [])

    let finalLauncherScriptInDestDir = Path.Combine(binDestDir.FullName, launcherScriptFile.Name) |> FileInfo
    if not (Directory.Exists(finalLauncherScriptInDestDir.Directory.FullName)) then
        Directory.CreateDirectory(finalLauncherScriptInDestDir.Directory.FullName) |> ignore
    File.Copy(launcherScriptFile.FullName, finalLauncherScriptInDestDir.FullName, true)
    if Process.Execute({ Command = "chmod"; Arguments = sprintf "ugo+x %s" finalLauncherScriptInDestDir.FullName },
                        Echo.Off).ExitCode <> 0 then
        failwith "Unexpected chmod failure, please report this bug"

| Some("run") ->
    let buildConfig = MakeAll None
    RunFrontend buildConfig None
        |> ignore

| Some "update-servers" ->
    let buildConfig = MakeAll None
    Directory.SetCurrentDirectory (GetPathToBackend())
    let proc1 = RunFrontend buildConfig (Some "--update-servers-file")
    if proc1.ExitCode <> 0 then
        Environment.Exit proc1.ExitCode
    else
        let proc2 = RunFrontend buildConfig (Some "--update-servers-stats")
        Environment.Exit proc2.ExitCode

| Some "strict" ->
    MakeAll <| Some "STRICTER_COMPILATION_BUT_WITH_REFLECTION_AT_RUNTIME"
        |> ignore

| Some "sanitycheck" ->
    let FindOffendingPrintfUsage () =
        let findScript = Path.Combine(rootDir.FullName, "scripts", "find.fsx")
        let fsxRunner =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows ->
                Path.Combine(rootDir.FullName, "scripts", "fsi.bat")
            | _ ->
                let fsxRunnerEnvVar = Environment.GetEnvironmentVariable "FsxRunner"
                if String.IsNullOrEmpty fsxRunnerEnvVar then
                    failwith "FsxRunner env var should have been passed to make.sh"
                fsxRunnerEnvVar
        let excludeFolders =
            String.Format("GWallet.Frontend.Console{0}" +
                          "GWallet.Backend.Tests.Unit{0}" +
                          "GWallet.Backend.Tests.End2End{0}" +
                          "GWallet.Backend{1}FSharpUtil.fs",
                          Path.PathSeparator, Path.DirectorySeparatorChar)

        let proc =
            {
                Command = fsxRunner
                Arguments = sprintf "%s --exclude=%s %s"
                                    findScript
                                    excludeFolders
                                    "printf failwithf"
            }
        let currentDir = Directory.GetCurrentDirectory()
        let srcDir = Path.Combine(currentDir, "src")
        Directory.SetCurrentDirectory srcDir
        let findProc = Process.SafeExecute (proc, Echo.All)
        Directory.SetCurrentDirectory currentDir
        if findProc.Output.StdOut.Trim().Length > 0 then
            Console.Error.WriteLine "Illegal usage of printf/printfn/sprintf/sprintfn/failwithf detected; use SPrintF1/SPrintF2/... instead"
            Environment.Exit 1

    let NotSubmodule (dir: DirectoryInfo): bool =
        let getSubmoduleDirsForThisRepo(): seq<DirectoryInfo> =
            let regex = Regex("path\s*=\s*([^\s]+)")
            seq {
                for regexMatch in regex.Matches (File.ReadAllText (".gitmodules")) do
                    let submoduleFolderRelativePath = regexMatch.Groups.[1].ToString()
                    let submoduleFolder =
                        DirectoryInfo (
                            Path.Combine (Directory.GetCurrentDirectory(), submoduleFolderRelativePath)
                        )
                    yield submoduleFolder
            }
        not (getSubmoduleDirsForThisRepo().Any(fun d -> dir.FullName = d.FullName))

    let SanityCheckNugetPackages (sol: FileInfo) =
        let rec findPackagesDotConfigFiles (dir: DirectoryInfo): seq<FileInfo> =
            dir.Refresh()
            seq {
                for file in (dir.EnumerateFiles()) do
                    if file.Name.ToLower() = "packages.config" then
                        yield file
                for subdir in (dir.EnumerateDirectories().Where (fun d -> NotSubmodule d)) do
                    for file in findPackagesDotConfigFiles subdir do
                        yield file
            }

        let getPackageTree (packagesConfigFiles: seq<FileInfo>): Map<ComparableFileInfo,seq<PackageInfo>> =
            seq {
                for packagesConfigFile in packagesConfigFiles do
                    let xmlDoc = XDocument.Load packagesConfigFile.FullName
                    for descendant in xmlDoc.Descendants() do
                        if descendant.Name.LocalName.ToLower() = "package" then
                            let id = descendant.Attributes().Single(fun attr -> attr.Name.LocalName = "id").Value
                            let version = descendant.Attributes().Single(fun attr -> attr.Name.LocalName = "version").Value
                            yield ({ File = packagesConfigFile }, { PackageId = id; PackageVersion = version })
            } |> MapHelper.MergeIntoMap

        let getAllPackageIdsAndVersions (packageTree: Map<ComparableFileInfo,seq<PackageInfo>>): Map<PackageInfo,seq<Project>> =
            seq {
                for KeyValue (packagesConfigFile, pkgs) in packageTree do
                    for pkg in pkgs do
                        yield (pkg, { FolderName = packagesConfigFile.File.Directory.Name })
            } |> MapHelper.MergeIntoMap

        let getDirectoryNamesForPackagesSet (packages: Map<PackageInfo,seq<Project>>): Map<string,seq<Project>> =
            seq {
                for KeyValue (package,prjs) in packages do
                    yield (sprintf "%s.%s" package.PackageId package.PackageVersion), prjs
            } |> Map.ofSeq

        let findMissingPackageDirs (solDir: DirectoryInfo) (idealPackageDirs: Map<string,seq<Project>>): Map<string,seq<Project>> =
            solDir.Refresh()
            let packagesSubFolder = DirectoryInfo(Path.Combine(solDir.FullName, "packages"))
            if not (packagesSubFolder.Exists) then
                failwith (sprintf "'packages' subdir under solution dir %s doesn't exist" packagesSubFolder.FullName)
            let packageDirsAbsolutePaths = packagesSubFolder.EnumerateDirectories().Select(fun dir -> dir.FullName)
            if not (packageDirsAbsolutePaths.Any()) then
                Console.Error.WriteLine(sprintf "'packages' subdir under solution dir %s doesn't contain any packages"
                                                packagesSubFolder.FullName)
                Console.Error.WriteLine("Forgot to `git submodule update --init`?")
                Environment.Exit(4)

            seq {
                for KeyValue(packageDirNameThatShouldExist,prjs) in idealPackageDirs do
                    if not (packageDirsAbsolutePaths.Contains(Path.Combine(packagesSubFolder.FullName, packageDirNameThatShouldExist))) then
                        yield packageDirNameThatShouldExist,prjs
            } |> Map.ofSeq

        let findExcessPackageDirs (solDir: DirectoryInfo) (idealPackageDirs: Map<string,seq<Project>>): seq<string> =
            solDir.Refresh()
            let packagesSubFolder = DirectoryInfo(Path.Combine(solDir.FullName, "packages"))
            if not (packagesSubFolder.Exists) then
                failwith (sprintf "'packages' subdir under solution dir %s doesn't exist, run 'make' first" packagesSubFolder.FullName)
            // "src" is a directory for source codes and build scripts,
            // not for packages, so we need to exclude it from here
            let packageDirNames = packagesSubFolder.EnumerateDirectories().Select(fun dir -> dir.Name).Except(["src"])
            if not (packageDirNames.Any()) then
                failwith (sprintf "'packages' subdir under solution dir %s doesn't contain any packages" packagesSubFolder.FullName)
            let packageDirsThatShouldExist = MapHelper.GetKeysOfMap idealPackageDirs
            seq {
                for packageDirThatExists in packageDirNames do
                    if not (packageDirsThatShouldExist.Contains packageDirThatExists) then
                        yield packageDirThatExists
            }

        let findPackagesWithMoreThanOneVersion
            (packageTree: Map<ComparableFileInfo,seq<PackageInfo>>)
            : Map<string,seq<ComparableFileInfo*PackageInfo>> =

            let getAllPackageInfos (packages: Map<ComparableFileInfo,seq<PackageInfo>>) =
                let pkgInfos =
                    seq {
                        for KeyValue (_,pkgs) in packages do
                            for pkg in pkgs do
                                yield pkg
                    }
                Set pkgInfos

            let getAllPackageVersionsForPackageId (packages: seq<PackageInfo>) (packageId: string) =
                Set(seq {
                    for package in packages do
                        if package.PackageId = packageId then
                            yield package.PackageVersion
                })

            let packageInfos = getAllPackageInfos packageTree
            let packageIdsWithMoreThan1Version = seq {
                for packageId in packageInfos.Select (fun pkg -> pkg.PackageId) do
                    let versions = getAllPackageVersionsForPackageId packageInfos packageId
                    if versions.Count > 1 then
                        yield packageId
            }
            if not (packageIdsWithMoreThan1Version.Any()) then
                Map.empty
            else
                let result = seq {
                    for pkgId in packageIdsWithMoreThan1Version do
                        let pkgs = seq {
                            for KeyValue (file,packageInfos) in packageTree do
                                for pkg in packageInfos do
                                    if pkg.PackageId = pkgId then
                                        yield file, pkg
                        }
                        yield pkgId, pkgs
                }
                Map.ofSeq result


        let solDir = sol.Directory
        solDir.Refresh ()
        let packageConfigFiles = findPackagesDotConfigFiles solDir
        let packageTree = getPackageTree packageConfigFiles
        let packages = getAllPackageIdsAndVersions packageTree
        Console.WriteLine(sprintf "%d nuget packages found for solution in directory %s" packages.Count solDir.Name)
        let idealDirList = getDirectoryNamesForPackagesSet packages

        let missingPackageDirs = findMissingPackageDirs solDir idealDirList
        if missingPackageDirs.Any () then
            for KeyValue(missingPkg, projects) in missingPackageDirs do
                let projectFolderNames = String.Join(",", projects.Select(fun p -> p.FolderName))
                Console.Error.WriteLine (sprintf "Missing folder for nuget package in submodule: %s (referenced from projects %s)" missingPkg projectFolderNames)
            Environment.Exit 1

        let excessPackageDirs = findExcessPackageDirs solDir idealDirList
        if excessPackageDirs.Any() then
            let advice = "remove it with git filter-branch to avoid needless bandwidth: http://stackoverflow.com/a/17824718/6503091"
            for excessPkg in excessPackageDirs do
                Console.Error.WriteLine(sprintf "Unused nuget package folder: %s (%s)" excessPkg advice)
            Environment.Exit 2

        let pkgWithMoreThan1VersionPrint (key: string) (packageInfos: seq<ComparableFileInfo*PackageInfo>) =
            Console.Error.WriteLine(sprintf "Package found with more than one version: %s. All occurrences:" key)
            for file,pkgInfo in packageInfos do
                Console.Error.WriteLine(sprintf "* Version: %s. Project folder: %s" pkgInfo.PackageVersion file.File.Directory.Name)
        let packagesWithMoreThanOneVersion = findPackagesWithMoreThanOneVersion packageTree
        if packagesWithMoreThanOneVersion.Any() then
            Map.iter pkgWithMoreThan1VersionPrint packagesWithMoreThanOneVersion
            Environment.Exit 3

        Console.WriteLine(sprintf "Nuget sanity check succeeded for solution dir %s" solDir.FullName)

    let rec FindSolutions (dir: DirectoryInfo): seq<FileInfo> =
        dir.Refresh()
        seq {
            // FIXME: avoid returning duplicates? (in case there are 2 .sln files in the same dir...)
            for file in (dir.EnumerateFiles()) do
                if file.Name.ToLower().EndsWith ".sln" then
                    yield file
            for subdir in (dir.EnumerateDirectories().Where (fun d -> NotSubmodule d)) do
                for solutionDir in FindSolutions subdir do
                    yield solutionDir
        }

    FindOffendingPrintfUsage()

    let solutions = Directory.GetCurrentDirectory() |> DirectoryInfo |> FindSolutions
    for sol in solutions do
        SanityCheckNugetPackages sol

| Some(someOtherTarget) ->
    Console.Error.WriteLine("Unrecognized target: " + someOtherTarget)
    Environment.Exit 2

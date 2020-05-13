namespace GWallet.Backend.UtxoCoin.Lightning

open System
open System.IO
open System.Net

open NBitcoin
open DotNetLightning.Channel
open DotNetLightning.Utils
open DotNetLightning.Chain
open DotNetLightning.Crypto
open DotNetLightning.Serialize
open DotNetLightning.Transactions

open GWallet.Backend

open Newtonsoft.Json


module JsonMarshalling =

    type CommitmentSpecConverter() =
        inherit JsonConverter<CommitmentSpec>()

        override this.ReadJson(reader: JsonReader, _: Type, _: CommitmentSpec, _: bool, serializer: JsonSerializer) =
            let serializedCommitmentSpec = serializer.Deserialize<SerializedCommitmentSpec> reader
            {
                CommitmentSpec.HTLCs = serializedCommitmentSpec.HTLCs
                FeeRatePerKw = serializedCommitmentSpec.FeeRatePerKw
                ToLocal = serializedCommitmentSpec.ToLocal
                ToRemote = serializedCommitmentSpec.ToRemote
            }

        override this.WriteJson(writer: JsonWriter, spec: CommitmentSpec, serializer: JsonSerializer) =
            let serializedCommitmentSpec =
                {
                    SerializedCommitmentSpec.HTLCs = spec.HTLCs
                    FeeRatePerKw = spec.FeeRatePerKw
                    ToLocal = spec.ToLocal
                    ToRemote = spec.ToRemote
                }
            serializer.Serialize(writer, serializedCommitmentSpec)

    type FeatureBitJsonConverter() =
        inherit JsonConverter<FeatureBit>()

        override this.ReadJson(reader: JsonReader, _: Type, _: FeatureBit, _: bool, serializer: JsonSerializer): FeatureBit =
            let serializedFeatureBit = serializer.Deserialize<string> reader
            let parsed = FeatureBit.TryParse serializedFeatureBit
            match parsed with
            | Ok featureBit -> featureBit
            | _ -> failwith "error decoding feature bit"

        override this.WriteJson(writer: JsonWriter, state: FeatureBit, serializer: JsonSerializer) =
            serializer.Serialize(writer, state.ToString())

    type IPAddressJsonConverter() =
        inherit JsonConverter<IPAddress>()

        override this.ReadJson(reader: JsonReader, _: Type, _: IPAddress, _: bool, serializer: JsonSerializer) =
            let serializedIPAddress = serializer.Deserialize<string> reader
            IPAddress.Parse serializedIPAddress

        override this.WriteJson(writer: JsonWriter, state: IPAddress, serializer: JsonSerializer) =
            serializer.Serialize(writer, state.ToString())

    type IPEndPointJsonConverter() =
        inherit JsonConverter<IPEndPoint>()

        override this.ReadJson(reader: JsonReader, _: Type, _: IPEndPoint, _: bool, serializer: JsonSerializer) =
            assert (reader.TokenType = JsonToken.StartArray)
            reader.Read() |> ignore
            let ip = serializer.Deserialize<IPAddress> reader
            reader.Read() |> ignore
            let port = serializer.Deserialize<int32> reader
            assert (reader.TokenType = JsonToken.EndArray)
            reader.Read() |> ignore
            IPEndPoint (ip, port)

        override this.WriteJson(writer: JsonWriter, state: IPEndPoint, serializer: JsonSerializer) =
            writer.WriteStartArray()
            serializer.Serialize(writer, state.Address)
            serializer.Serialize(writer, state.Port)
            writer.WriteEndArray()

    let LightningSerializerSettings: JsonSerializerSettings =
        let settings = Marshalling.DefaultSettings ()
        let converter = CommitmentsJsonConverter()
        let ipAddressConverter = IPAddressJsonConverter()
        let ipEndPointConverter = IPEndPointJsonConverter()
        let featureBitConverter = FeatureBitJsonConverter()
        settings.Converters.Add converter
        settings.Converters.Add ipAddressConverter
        settings.Converters.Add ipEndPointConverter
        settings.Converters.Add featureBitConverter
        NBitcoin.JsonConverters.Serializer.RegisterFrontConverters settings
        settings

    let ConfigDir: DirectoryInfo = Config.GetConfigDir Currency.BTC AccountKind.Normal
    let LightningDir: DirectoryInfo = Path.Combine (ConfigDir.FullName, "LN") |> DirectoryInfo

    let SaveSerializedChannel (serializedChannel: SerializedChannel)
                              (fileName: string) =
        let json = Marshalling.SerializeCustom(serializedChannel, LightningSerializerSettings)
        let filePath = Path.Combine (LightningDir.FullName, fileName)
        if not LightningDir.Exists then
            LightningDir.Create()
        File.WriteAllText (filePath, json)
        ()

    let Save (account: NormalAccount)
             (chan: Channel)
             (keysRepoSeed: uint256)
             (ip: IPEndPoint)
             (minSafeDepth: BlockHeightOffset32)
                 : string -> unit =
        SaveSerializedChannel
            {
                KeysRepoSeed = keysRepoSeed
                Network = chan.Network
                RemoteNodeId = chan.RemoteNodeId.Value
                ChanState = chan.State
                AccountFileName = account.AccountFile.Name
                CounterpartyIP = ip
                MinSafeDepth = minSafeDepth
            }

    let LoadSerializedChannel (fileName: string): SerializedChannel =
        let json = File.ReadAllText fileName
        Marshalling.DeserializeCustom<SerializedChannel> (json, LightningSerializerSettings)

    let DummyProvideFundingTx (_ : IDestination * Money * FeeRatePerKw): FSharp.Core.Result<(FinalizedTx * TxOutIndex),string> =
        FSharp.Core.Result.Error "funding tx not needed cause channel already created"

    let UIntToKeyRepo (channelKeysSeed: uint256): DefaultKeyRepository =
        let littleEndian = channelKeysSeed.ToBytes()
        DefaultKeyRepository (ExtKey littleEndian, 0)

    let ChannelFromSerialized (serializedChannel: SerializedChannel) (createChannel) (feeEstimator): Channel =
        let keyRepo = UIntToKeyRepo serializedChannel.KeysRepoSeed
        {
            createChannel
                keyRepo
                feeEstimator
                keyRepo.NodeSecret.PrivateKey
                DummyProvideFundingTx
                serializedChannel.Network
                (NodeId serializedChannel.RemoteNodeId)
            with State = serializedChannel.ChanState
        }
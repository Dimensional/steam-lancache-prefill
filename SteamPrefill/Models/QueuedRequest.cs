namespace SteamPrefill.Models
{
    public struct QueuedRequest
    {
        public readonly uint AppId;
        public readonly uint DepotId;
        public readonly ulong ManifestId;

        /// <summary>
        /// The SHA-1 hash of the chunk's id.
        /// </summary>
        public string ChunkId;

        /// <summary>
        /// The content-length of the data to be requested.
        /// </summary>
        public readonly uint CompressedLength;

        public Exception LastFailureReason { get; set; }

        public QueuedRequest(uint appId, Manifest depotManifest, ChunkData chunk)
        {
            AppId = appId;
            DepotId = depotManifest.DepotId;
            ChunkId = chunk.ChunkId;
            CompressedLength = chunk.CompressedLength;

            ManifestId = depotManifest.Id;
        }

        public string OutputDir => Path.Combine(AppConfig.TempDir, "Depots", DepotId.ToString(), $"{ChunkId}.bin");

        public override string ToString()
        {
            return $"{DepotId} - {ChunkId}";
        }
    }
}
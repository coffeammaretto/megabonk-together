using MemoryPack;

namespace MegabonkTogether.Common.Messages
{
    public static class SnapshotChunker
    {
        /// <summary>
        /// Serialize a snapshot, splitting its items over several messages so each one fits in <paramref name="maxBytes"/>.
        /// An item too big to fit on its own is returned alone with Fits = false.
        /// Only use this for snapshots whose items are applied independently by the receiver.
        /// </summary>
        public static List<(byte[] Data, bool Fits)> Serialize<TItem>(List<TItem> items, Func<List<TItem>, IGameNetworkMessage> createMessage, int maxBytes)
        {
            var result = new List<(byte[] Data, bool Fits)>();
            SerializeInto(items, createMessage, maxBytes, result);
            return result;
        }

        private static void SerializeInto<TItem>(List<TItem> items, Func<List<TItem>, IGameNetworkMessage> createMessage, int maxBytes, List<(byte[] Data, bool Fits)> result)
        {
            if (items.Count == 0)
            {
                return;
            }

            var data = MemoryPackSerializer.Serialize(createMessage(items));
            if (data.Length < maxBytes || items.Count == 1)
            {
                result.Add((data, data.Length < maxBytes));
                return;
            }

            // Aim for 3/4 of the budget so items of uneven size rarely need another split
            int chunkCount = data.Length / (maxBytes * 3 / 4) + 1;
            int chunkSize = (items.Count + chunkCount - 1) / chunkCount;

            for (int i = 0; i < items.Count; i += chunkSize)
            {
                SerializeInto(items.GetRange(i, Math.Min(chunkSize, items.Count - i)), createMessage, maxBytes, result);
            }
        }
    }
}

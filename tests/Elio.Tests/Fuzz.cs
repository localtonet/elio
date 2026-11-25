namespace Elio.Tests;

/// <summary>
/// What the tests that feed a parser broken, cut short or hostile input have in common. Every run is seeded, so a
/// failure names its seed and can be repeated exactly.
/// </summary>
internal static class Fuzz
{
    /// <summary>How long one piece of input may keep a parser busy before it counts as hanging.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>Bytes of any value, of a length up to <paramref name="maxLength"/>.</summary>
    public static byte[] Garbage(Random random, int maxLength)
    {
        byte[] data = new byte[random.Next(maxLength + 1)];
        random.NextBytes(data);
        return data;
    }

    /// <summary>Bytes drawn from a few that mean something to the parser, which reaches far more of it than noise does.</summary>
    public static byte[] Garbage(Random random, int maxLength, ReadOnlySpan<byte> alphabet)
    {
        byte[] data = new byte[random.Next(maxLength + 1)];
        for (int i = 0; i < data.Length; i++) data[i] = random.Next(4) == 0 ? (byte)random.Next(256) : alphabet[random.Next(alphabet.Length)];
        return data;
    }

    /// <summary>A copy of good input with a few things done to it: bytes changed, dropped, repeated or added, or the end cut off.</summary>
    public static byte[] Mutate(Random random, byte[] valid)
    {
        var data = new List<byte>(valid);
        int changes = 1 + random.Next(4);
        for (int c = 0; c < changes && data.Count > 0; c++)
        {
            int at = random.Next(data.Count);
            switch (random.Next(6))
            {
                case 0:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    data[at] = (byte)random.Next(256);
                    break;
                case 2:
                    data.RemoveRange(at, Math.Min(data.Count - at, 1 + random.Next(16)));
                    break;
                case 3:
                    data.InsertRange(at, data.GetRange(at, Math.Min(data.Count - at, 1 + random.Next(16))));
                    break;
                case 4:
                    data.InsertRange(at, Garbage(random, 16));
                    break;
                default:
                    data.RemoveRange(at, data.Count - at);
                    break;
            }
        }

        return [.. data];
    }

    /// <summary>Cuts input into pieces of random sizes, the way a network hands it over.</summary>
    public static IEnumerable<byte[]> Pieces(Random random, byte[] data, int maxPiece = 64)
    {
        for (int at = 0; at < data.Length;)
        {
            int length = Math.Min(data.Length - at, 1 + random.Next(maxPiece));
            yield return data[at..(at + length)];
            at += length;
        }
    }

    /// <summary>Runs work that must finish on its own, failing the test if it does not finish in time.</summary>
    public static void Within(Action action, TimeSpan? limit = null)
    {
        Task work = Task.Run(action);
        if (!work.Wait(limit ?? Patience)) Assert.Fail($"Still running after {(limit ?? Patience).TotalSeconds:0} seconds: the input made it hang.");
        work.GetAwaiter().GetResult();
    }

    /// <summary>The bytes the calling thread allocated while running the action.</summary>
    public static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

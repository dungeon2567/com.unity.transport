using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Baselib.LowLevel;
using Unity.Burst;
using Unity.Collections;

using Random = Unity.Mathematics.Random;

namespace Unity.Networking.Transport.Utilities
{
    /// <summary>
    /// A NativeMultiQueue is a set of several FIFO queues split into buckets.
    /// Each bucket has its own first and last item and each bucket can have
    /// items pushed and popped individually.
    /// </summary>
    internal struct NativeMultiQueue<T> : IDisposable where T : unmanaged
    {
        private NativeList<T> m_Queue;
        private NativeList<int> m_QueueHeadTail;
        private NativeArray<int> m_MaxItems;

        public bool IsCreated => m_Queue.IsCreated;

        /// <summary>
        /// New NativeMultiQueue has a single bucket and the specified number
        /// of items for that bucket. Accessing buckets out of range will grow
        /// the number of buckets and pushing more items than the initial capacity
        /// will increase the number of items for each bucket.
        /// </summary>
        public NativeMultiQueue(int initialMessageCapacity)
        {
            m_MaxItems = new NativeArray<int>(1, Allocator.Persistent);
            m_MaxItems[0] = initialMessageCapacity;
            m_Queue = new NativeList<T>(initialMessageCapacity, Allocator.Persistent);
            m_QueueHeadTail = new NativeList<int>(2, Allocator.Persistent);
        }

        public void Dispose()
        {
            m_MaxItems.Dispose();
            m_Queue.Dispose();
            m_QueueHeadTail.Dispose();
        }

        /// <summary>
        /// Enqueue a new item to a specific bucket. If the bucket does not yet exist
        /// the number of buckets will be increased and if the queue is full the number
        /// of items for each bucket will be increased.
        /// </summary>
        public void Enqueue(int bucket, T value)
        {
            // Grow number of buckets to fit specified index
            if (bucket >= m_QueueHeadTail.Length / 2)
            {
                int oldSize = m_QueueHeadTail.Length;
                m_QueueHeadTail.ResizeUninitialized((bucket + 1) * 2);
                for (; oldSize < m_QueueHeadTail.Length; ++oldSize)
                    m_QueueHeadTail[oldSize] = 0;
                m_Queue.ResizeUninitialized((m_QueueHeadTail.Length / 2) * m_MaxItems[0]);
            }
            int idx = m_QueueHeadTail[bucket * 2 + 1];
            if (idx >= m_MaxItems[0])
            {
                // Grow number of items per bucket
                int oldMax = m_MaxItems[0];
                while (idx >= m_MaxItems[0])
                    m_MaxItems[0] = m_MaxItems[0] * 2;
                int maxBuckets = m_QueueHeadTail.Length / 2;
                m_Queue.ResizeUninitialized(maxBuckets * m_MaxItems[0]);
                for (int b = maxBuckets - 1; b >= 0; --b)
                {
                    for (int i = m_QueueHeadTail[b * 2 + 1] - 1; i >= m_QueueHeadTail[b * 2]; --i)
                    {
                        m_Queue[b * m_MaxItems[0] + i] = m_Queue[b * oldMax + i];
                    }
                }
            }
            m_Queue[m_MaxItems[0] * bucket + idx] = value;
            m_QueueHeadTail[bucket * 2 + 1] = idx + 1;
        }

        /// <summary>
        /// Dequeue an item from a specific bucket. If the bucket does not exist or if the
        /// bucket is empty the call will fail and return false.
        /// </summary>
        public bool Dequeue(int bucket, out T value)
        {
            if (bucket < 0 || bucket >= m_QueueHeadTail.Length / 2)
            {
                value = default;
                return false;
            }
            int idx = m_QueueHeadTail[bucket * 2];
            if (idx >= m_QueueHeadTail[bucket * 2 + 1])
            {
                m_QueueHeadTail[bucket * 2] = m_QueueHeadTail[bucket * 2 + 1] = 0;
                value = default;
                return false;
            }
            else if (idx + 1 == m_QueueHeadTail[bucket * 2 + 1])
            {
                m_QueueHeadTail[bucket * 2] = m_QueueHeadTail[bucket * 2 + 1] = 0;
            }
            else
            {
                m_QueueHeadTail[bucket * 2] = idx + 1;
            }

            value = m_Queue[m_MaxItems[0] * bucket + idx];
            return true;
        }

        /// <summary>
        /// Peek the next item in a specific bucket. If the bucket does not exist or if the
        /// bucket is empty the call will fail and return false.
        /// </summary>
        public bool Peek(int bucket, out T value)
        {
            if (bucket < 0 || bucket >= m_QueueHeadTail.Length / 2)
            {
                value = default;
                return false;
            }
            int idx = m_QueueHeadTail[bucket * 2];
            if (idx >= m_QueueHeadTail[bucket * 2 + 1])
            {
                value = default;
                return false;
            }

            value = m_Queue[m_MaxItems[0] * bucket + idx];
            return true;
        }

        /// <summary>
        /// Remove all items from a specific bucket. If the bucket does not exist
        /// the call will not do anything.
        /// </summary>
        public void Clear(int bucket)
        {
            if (bucket < 0 || bucket >= m_QueueHeadTail.Length / 2)
                return;
            m_QueueHeadTail[bucket * 2] = 0;
            m_QueueHeadTail[bucket * 2 + 1] = 0;
        }
    }

    public static class SequenceHelpers
    {
        // Calculate difference between the sequence IDs taking into account wrapping, so when you go from 65535 to 0 the distance is 1
        public static int AbsDistance(ushort lhs, ushort rhs)
        {
            int distance;
            if (lhs < rhs)
                distance = lhs + ushort.MaxValue + 1 - rhs;
            else
                distance = lhs - rhs;
            return distance;
        }

        public static bool IsNewer(uint current, uint old)
        {
            // Invert the check so same does not count as newer
            return !(old - current < (1u << 31));
        }

        public static bool GreaterThan16(ushort lhs, ushort rhs)
        {
            const uint max_sequence_divide_2 = 0x7FFF;
            return lhs > rhs && lhs - rhs <= (ushort)max_sequence_divide_2 ||
                lhs < rhs && rhs - lhs > (ushort)max_sequence_divide_2;
        }

        public static bool LessThan16(ushort lhs, ushort rhs)
        {
            return GreaterThan16(rhs, lhs);
        }

        public static bool StalePacket(ushort sequence, ushort oldSequence, ushort windowSize)
        {
            return LessThan16(sequence, (ushort)(oldSequence - windowSize));
        }

        public static string BitMaskToString(uint mask)
        {
            //  31     24      16      8       0
            // |-------+-------+-------+--------|
            //  00000000000000000000000000000000

            const int bits = 4 * 8;
            var sb = new char[bits];

            for (var i = bits - 1; i >= 0; i--)
            {
                sb[i] = (mask & 1) != 0 ? '1' : '0';
                mask >>= 1;
            }

            return new string(sb);
        }
    }

    public static class FixedStringHexExt
    {
        public static FormatError AppendHex<T>(ref this T str, ushort val) where T : unmanaged, INativeList<byte>, IUTF8Bytes
        {
            int shamt = 12;
            // Find the first non-zero nibble
            while (shamt > 0)
            {
                if (((val >> shamt) & 0xf) != 0)
                    break;
                shamt -= 4;
            }
            var err = FormatError.None;
            while (shamt >= 0)
            {
                var nibble = (val >> shamt) & 0xf;
                if (nibble >= 10)
                    err |= str.AppendRawByte((byte)('a' + nibble - 10));
                else
                    err |= str.AppendRawByte((byte)('0' + nibble));
                shamt -= 4;
            }
            return err != FormatError.None ? FormatError.Overflow : FormatError.None;
        }
        
        public static FormatError AppendHex2<T>(ref this T str, ushort val) where T : unmanaged, INativeList<byte>, IUTF8Bytes
        {
            if (val <= 0xf)
            {
                if (str.Append('0') == FormatError.Overflow)
                    return FormatError.Overflow;
            }

            return str.AppendHex(val);
        }
    }

    public static class NativeListExt
    {
        /// <summary>
        /// This function will make sure that <see cref="sizeToFit"/> can fit into <see cref="list"/>.
        /// If <see cref="sizeToFit"/> >= <see cref="list"/>'s Length then <see cref="list"/> will be ResizeUninitialized to a new length.
        /// New Length will be the next highest power of 2 of <see cref="sizeToFit"/>
        /// </summary>
        /// <param name="list">List that should be resized if sizeToFit >= its size</param>
        /// <param name="sizeToFit">Requested size that should fit into list</param>
        public static void ResizeUninitializedTillPowerOf2<T>(this NativeList<T> list, int sizeToFit) where T : unmanaged
        {
            var n = list.Capacity;

            if (sizeToFit >= n)
            {
                // https://graphics.stanford.edu/~seander/bithacks.html#RoundUpPowerOf2
                sizeToFit |= sizeToFit >> 1;
                sizeToFit |= sizeToFit >> 2;
                sizeToFit |= sizeToFit >> 4;
                sizeToFit |= sizeToFit >> 8;
                sizeToFit |= sizeToFit >> 16;
                sizeToFit++;
                //sizeToFit is now next power of 2 of initial sizeToFit

                list.Capacity = sizeToFit;
            }
        }
    }

    public static class RandomHelpers
    {
        private static readonly SharedStatic<long> s_SharedSeed = SharedStatic<long>.GetOrCreate<SharedRandomKey>(16);
        private class SharedRandomKey {}

        static RandomHelpers()
        {
            s_SharedSeed.Data = 0;
        }

        internal static Unity.Mathematics.Random GetRandomGenerator()
        {
            // if the seed has not been initialized we set it to the current ticks.
            if (s_SharedSeed.Data == 0)
                Interlocked.CompareExchange(ref s_SharedSeed.Data, (long)TimerHelpers.GetTicks(), 0);

            // otherwise we just increment it, ensuring we get a different value for every call.
            var seed = (Interlocked.Increment(ref s_SharedSeed.Data) % uint.MaxValue) + 1;

            return new Mathematics.Random((uint)seed);
        }

        // returns ushort in [1..ushort.MaxValue] range
        public static ushort GetRandomUShort()
        {
            return (ushort)GetRandomGenerator().NextUInt(1, ushort.MaxValue - 1);
        }

        // returns ulong in [1..ulong.MaxValue] range
        public static ulong GetRandomULong()
        {
            var random = GetRandomGenerator();
            var high = random.NextUInt(0, uint.MaxValue - 1);
            var low = random.NextUInt(1, uint.MaxValue - 1);
            return ((ulong)high << 32) | (ulong)low;
        }

        internal unsafe static ConnectionToken GetRandomConnectionToken()
        {
            var token = new ConnectionToken();
            var random = GetRandomGenerator();

            for (int i = 0; i < ConnectionToken.k_Length; i++)
                token.Value[i] = (byte)(random.NextUInt() & 0xFF);

            return token;
        }
    }

    internal static class TimerHelpers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong GetTicks()
        {
            return Binding.Baselib_Timer_GetHighPrecisionTimerTicks();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long GetCurrentTimestampMS()
        {
            // Normally we'd use Baselib_Timer_GetTicksToNanosecondsConversionRatio for more precise
            // timestamp calculations, but it can't be used in DOTS Runtime (yet) because its
            // bindings link directly to the version returning a struct, whereas normal bindings use
            // the injected version that returns the struct through an out parameter.
            return (long)(Binding.Baselib_Timer_GetTimeSinceStartupInSeconds() * 1000);
        }

        // Used in tests to sleep inside Burst-compiled code.
        internal static void Sleep(uint ms)
        {
            Binding.Baselib_Timer_WaitForAtLeast(ms);
        }
    }
}

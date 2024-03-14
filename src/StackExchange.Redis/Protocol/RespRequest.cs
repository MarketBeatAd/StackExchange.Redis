﻿using Pipelines.Sockets.Unofficial.Arenas;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS1591 // new API

namespace StackExchange.Redis.Protocol;

[Experimental(ExperimentalDiagnosticID)]
public abstract class RespRequest
{
    internal const string ExperimentalDiagnosticID = "SERED002";
    protected RespRequest() { }
    public abstract void Write(ref Resp2Writer writer);
}

[Experimental(RespRequest.ExperimentalDiagnosticID)]
public enum RespPrefix : byte
{
    None = 0,
    SimpleString = (byte)'+',
    SimpleError = (byte)'-',
    Integer = (byte)':',
    BulkString = (byte)'$',
    Array = (byte)'*',
    Null = (byte)'_',
    Boolean = (byte)'#',
    Double = (byte)',',
    BigNumber = (byte)'(',
    BulkError = (byte)'!',
    VerbatimString = (byte)'=',
    Map = (byte)'%',
    Set = (byte)'~',
    Push = (byte)'>',

    // these are not actually implemented
    // Stream = (byte)';',
    // UnboundEnd = (byte)'.',
    // Attribute = (byte)'|',
}

//[Experimental(RespRequest.ExperimentalDiagnosticID)]
//public abstract class RespProcessor<T>
//{
//    public abstract T Parse(in RespChunk value);
//}

internal sealed class RefCountedSequenceSegment<T> : ReadOnlySequenceSegment<T>, IMemoryOwner<T>
{
    public override string ToString() => $"(ref-count: {RefCount}) {base.ToString()}";
    private int _refCount;
    internal int RefCount => Volatile.Read(ref _refCount);
    private static void ThrowDisposed() => throw new ObjectDisposedException(nameof(RefCountedSequenceSegment<T>));
    private sealed class DisposedMemoryManager : MemoryManager<T>
    {
        public static readonly ReadOnlyMemory<T> Instance = new DisposedMemoryManager().Memory;
        private DisposedMemoryManager() { }

        protected override void Dispose(bool disposing) { }
        public override Span<T> GetSpan() { ThrowDisposed(); return default; }
        public override Memory<T> Memory { get { ThrowDisposed(); return default; } }
        public override MemoryHandle Pin(int elementIndex = 0) { ThrowDisposed(); return default; }
        public override void Unpin() => ThrowDisposed();
        protected override bool TryGetArray(out ArraySegment<T> segment)
        {
            ThrowDisposed();
            segment = default;
            return default;
        }
    }

    public RefCountedSequenceSegment(int minSize, RefCountedSequenceSegment<T>? previous = null)
    {
        _refCount = 1;
        Memory = ArrayPool<T>.Shared.Rent(minSize);
        if (previous is not null)
        {
            RunningIndex = previous.RunningIndex + previous.Memory.Length;
            previous.Next = this;
        }
    }

    Memory<T> IMemoryOwner<T>.Memory => MemoryMarshal.AsMemory(Memory);

    public void Dispose()
    {
        int oldCount;
        do
        {
            oldCount = Volatile.Read(ref _refCount);
            if (oldCount == 0) return; // already released
        } while (Interlocked.CompareExchange(ref _refCount, oldCount - 1, oldCount) != oldCount);
        if (oldCount == 0) // we killed it
        {
            Release();
        }
    }

    public void AddRef()
    {
        int oldCount;
        do
        {
            oldCount = Volatile.Read(ref _refCount);
            if (oldCount == 0) ThrowDisposed();
        } while (Interlocked.CompareExchange(ref _refCount, checked(oldCount + 1), oldCount) != oldCount);
    }

    private void Release()
    {
        var memory = Memory;
        Memory = DisposedMemoryManager.Instance;
        if (MemoryMarshal.TryGetArray<T>(memory, out var segment) && segment.Array is not null)
        {
            ArrayPool<T>.Shared.Return(segment.Array);
        }
    }

    internal new RefCountedSequenceSegment<T>? Next
    {
        get => (RefCountedSequenceSegment<T>?)base.Next;
        set => base.Next = value;
    }
}

public readonly struct LeasedSequence<T> : IDisposable
{
    public LeasedSequence(ReadOnlySequence<T> value) => _value = value;
    private readonly ReadOnlySequence<T> _value;

    public override string ToString() => _value.ToString();
    public long Length => _value.Length;
    public bool IsEmpty => _value.IsEmpty;
    public bool IsSingleSegment => _value.IsSingleSegment;
    public SequencePosition Start => _value.Start;
    public SequencePosition End => _value.End;
    public SequencePosition GetPosition(long offset) => _value.GetPosition(offset);
    public SequencePosition GetPosition(long offset, SequencePosition origin) => _value.GetPosition(offset, origin);

    public ReadOnlyMemory<T> First => _value.First;
#if NETCOREAPP3_0_OR_GREATER
    public ReadOnlySpan<T> FirstSpan => _value.FirstSpan;
#else
    public ReadOnlySpan<T> FirstSpan => _value.First.Span;
#endif

    public bool TryGet(ref SequencePosition position, out ReadOnlyMemory<T> memory, bool advance = true)
        => _value.TryGet(ref position, out memory, advance);
    public ReadOnlySequence<T>.Enumerator GetEnumerator() => _value.GetEnumerator();

    public static implicit operator ReadOnlySequence<T>(LeasedSequence<T> value) => value._value;

    // we do *not* assume that slices take additional leases; usually slicing is a transient operation
    public ReadOnlySequence<T> Slice(long start) => _value.Slice(start);
    public ReadOnlySequence<T> Slice(SequencePosition start) => _value.Slice(start);
    public ReadOnlySequence<T> Slice(int start, int length) => _value.Slice(start, length);
    public ReadOnlySequence<T> Slice(int start, SequencePosition end) => _value.Slice(start, end);
    public ReadOnlySequence<T> Slice(long start, long length) => _value.Slice(start, length);
    public ReadOnlySequence<T> Slice(long start, SequencePosition end) => _value.Slice(start, end);
    public ReadOnlySequence<T> Slice(SequencePosition start, int length) => _value.Slice(start, length);
    public ReadOnlySequence<T> Slice(SequencePosition start, long length) => _value.Slice(start, length);
    public ReadOnlySequence<T> Slice(SequencePosition start, SequencePosition end) => _value.Slice(start, end);

    public void Dispose()
    {
        if (_value.Start.GetObject() is SequenceSegment<T> segment)
        {
            var end = _value.End.GetObject();
            do
            {
                if (segment is IDisposable d)
                {
                    d.Dispose();
                }
            }
            while (!ReferenceEquals(segment, end) && (segment = segment!.Next) is not null);
        }
    }

    public void AddRef()
    {
        if (_value.Start.GetObject() is SequenceSegment<T> segment)
        {
            var end = _value.End.GetObject();
            do
            {
                if (segment is RefCountedSequenceSegment<T> counted)
                {
                    counted.AddRef();
                }
            }
            while (!ReferenceEquals(segment, end) && (segment = segment!.Next) is not null);
        }
    }
}

/// <summary>
/// Abstract source of streaming RESP data; the implementation is responsible
/// for retaining a back buffer of pending bytes, and exposing those bytes via <see cref="GetBuffer"/>;
/// additional data is requested via <see cref="TryReadAsync(CancellationToken)"/>, and
/// is consumed via <see cref="Take(long)"/>. The data returned from <see cref="Take(long)"/>
/// can optionally be a chain of <see cref="SequenceSegment{T}"/> that additionally
/// implement <see cref="IDisposable"/>, in which case the <see cref="LeasedSequence{T}"/>
/// will dispose them appropriately (allowing for buffer pool scenarios). Note also that
/// the buffer returned from <see cref="Take"/> does not need to be the same chain as
/// used in <see cref="GetBuffer"/> - it is permitted to copy (etc) the data when consuming.
/// </summary>
[Experimental(RespRequest.ExperimentalDiagnosticID)]
public abstract class RespSource : IAsyncDisposable
{
    public static RespSource Create(Stream source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!source.CanRead) throw new ArgumentException("Source stream cannot be read", nameof(source));
        return new StreamRespSource(source);
    }

    protected abstract ReadOnlySequence<byte> GetBuffer();

    public static RespSource Create(ReadOnlySequence<byte> payload) => new InMemoryRespSource(payload);
    public static RespSource Create(ReadOnlyMemory<byte> payload) => new InMemoryRespSource(new(payload));

    private protected RespSource() { }

    protected abstract ValueTask<bool> TryReadAsync(CancellationToken cancellationToken);

    // internal abstract long Scan(long skip, ref int count);
    public async ValueTask<LeasedSequence<byte>> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        int pending = 1;
        long totalConsumed = 0;
        while (pending != 0)
        {
            var consumed = Scan(GetBuffer().Slice(totalConsumed), ref pending);
            totalConsumed += consumed;

            if (pending != 0 && !(await TryReadAsync(cancellationToken)))
            {
                if (totalConsumed != 0) throw new EndOfStreamException();
                return default;
            }
        }

        var chunk = Take(totalConsumed);
        if (chunk.Length != totalConsumed) Throw();
        return new(chunk);

        static void Throw() => throw new InvalidOperationException("Buffer length mismatch in " + nameof(ReadNextAsync));

        // can't use ref-struct in async method
        static long Scan(ReadOnlySequence<byte> payload, ref int count)
        {
            var reader = new RespReader(payload);
            while (count > 0 && reader.ReadNext())
            {
                count = count - 1 + reader.ChildCount;
            }
            return reader.BytesConsumed;
        }
    }

    protected abstract ReadOnlySequence<byte> Take(long bytes);

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }

    private sealed class InMemoryRespSource : RespSource
    {
        private ReadOnlySequence<byte> _remaining;
        public InMemoryRespSource(ReadOnlySequence<byte> value)
            => _remaining = value;

        protected override ReadOnlySequence<byte> GetBuffer() => _remaining;
        protected override ReadOnlySequence<byte> Take(long bytes)
        {
            var take = _remaining.Slice(0, bytes);
            _remaining = _remaining.Slice(take.End);
            return take;
        }
        protected override ValueTask<bool> TryReadAsync(CancellationToken cancellationToken) => default; // nothing more to get
    }

    private sealed class StreamRespSource : RespSource
    {
        private readonly Stream _source;

        private RotatingBufferCore _buffer;
        internal StreamRespSource(Stream source, int blockSize = 64 * 1024)
        {
            _buffer = new(Math.Max(1024, blockSize));
            _source = source;
        }

        protected override ReadOnlySequence<byte> GetBuffer() => _buffer.GetBuffer();


#if NETCOREAPP3_1_OR_GREATER
        public override ValueTask DisposeAsync() {
            _buffer.Dispose();
            return _source.DisposeAsync();
        }
#else
        public override ValueTask DisposeAsync()
        {
            _buffer.Dispose();
            _source.Dispose();
            return default;
        }
#endif
        protected override ValueTask<bool> TryReadAsync(CancellationToken cancellationToken)
        {
            var readBuffer = _buffer.GetWritableTail();
            Debug.Assert(!readBuffer.IsEmpty, "should have space");
#if NETCOREAPP3_1_OR_GREATER
            var pending = _source.ReadAsync(readBuffer, cancellationToken);
            if (!pending.IsCompletedSuccessfully) return Awaited(this, pending);
#else
            // we know it is an array; happy to explode weirdly otherwise!
            if (!MemoryMarshal.TryGetArray<byte>(readBuffer, out var segment)) ThrowNotArray();
            var pending = _source.ReadAsync(segment.Array, segment.Offset, segment.Count, cancellationToken);
            if (pending.Status != TaskStatus.RanToCompletion) return Awaited(this, pending);

            static void ThrowNotArray() => throw new InvalidOperationException("Unable to obtain array from tail buffer");
#endif

            // synchronous happy case
            var bytes = pending.GetAwaiter().GetResult();
            if (bytes > 0)
            {
                _buffer.Commit(bytes);
                return new(true);
            }
            return default;

            static async ValueTask<bool> Awaited(StreamRespSource @this,
#if NETCOREAPP3_1_OR_GREATER
                ValueTask<int> pending
#else
                Task<int> pending
#endif
                )
            {
                var bytes = await pending;
                if (bytes > 0)
                {
                    @this._buffer.Commit(bytes);
                    return true;
                }
                return false;
            }
        }

        protected override ReadOnlySequence<byte> Take(long bytes) => _buffer.DetachRotating(bytes);
    }
}

[Experimental(RespRequest.ExperimentalDiagnosticID)]
public ref struct RespReader
{
    private long _positionBase;
    private int _bufferIndex, _bufferLength;
    private RespPrefix _prefix;
    public readonly long BytesConsumed => _positionBase + _bufferIndex;
    public readonly RespPrefix Prefix => _prefix;

    private int _currentOffset, _currentLength;

    /// <summary>
    /// Returns as much data as possible into the buffer, ignoring
    /// any data that cannot fit into <paramref name="target"/>, and
    /// returning the segment representing copied data.
    /// </summary>
    public readonly Span<byte> CopyTo(Span<byte> target)
    {
        if (!IsScalar) return default; // only possible for scalars
        if (TryGetValueSpan(out var source))
        {
            if (source.Length > target.Length)
            {
                source = source.Slice(0, target.Length);
            }
            else if (source.Length < target.Length)
            {
                target = target.Slice(0, source.Length);
            }
            source.CopyTo(target);
            return target;
        }
        throw new NotImplementedException();
    }

    internal readonly bool TryGetValueSpan(out ReadOnlySpan<byte> span)
    {
        if (!IsScalar)
        {
            span = default;
            return false; // only possible for scalars
        }
        if (_currentOffset < 0) Throw();
        if (_currentLength == 0)
        {
            span = default;
        }
        else
        {
#if NET7_0_OR_GREATER
            span = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref _bufferRoot, _currentOffset), _currentLength);
#else
            span = _bufferSpan.Slice(_currentOffset, _currentLength);
#endif
        }
        return true;

        static void Throw() => throw new InvalidOperationException();
    }
    internal readonly string? ReadString()
    {
        if (IsNull()) return null;
        if (TryGetValueSpan(out var span))
        {
            if (span.IsEmpty) return "";
#if NETCOREAPP3_0_OR_GREATER
            return Resp2Writer.UTF8.GetString(span);
#else
            unsafe
            {
                fixed (byte* ptr = span)
                {
                    return Resp2Writer.UTF8.GetString(ptr, span.Length);
                }
            }
#endif
        }
        throw new NotImplementedException();
    }

#if NET7_0_OR_GREATER
    private ref byte _bufferRoot;
    private RespPrefix PeekPrefix() => (RespPrefix)Unsafe.Add(ref _bufferRoot, _bufferIndex);
    private ReadOnlySpan<byte> PeekPastPrefix() => MemoryMarshal.CreateReadOnlySpan(
        ref Unsafe.Add(ref _bufferRoot, _bufferIndex + 1), _bufferLength - (_bufferIndex + 1));
    private void AssertCrlfPastPrefixUnsafe(int offset)
    {
        if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref _bufferRoot, _bufferIndex + offset + 1)) != Resp2Writer.CrLf)
            ThrowProtocolFailure();
    }
    private void SetCurrent(ReadOnlyMemory<byte> current)
    {
        _positionBase += _bufferLength; // accumulate previous length
        _bufferRoot = ref MemoryMarshal.GetReference(current.Span);
        _bufferIndex = 0;
        _bufferLength = current.Length;
    }
#else
    private ReadOnlySpan<byte> _bufferSpan;
    private readonly RespPrefix PeekPrefix() => (RespPrefix)_bufferSpan[_bufferIndex];
    private readonly ReadOnlySpan<byte> PeekPastPrefix() => _bufferSpan.Slice(_bufferIndex + 1);
    private readonly void AssertCrlfPastPrefixUnsafe(int offset)
    {
        if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.AsRef(in _bufferSpan[_bufferIndex + offset + 1])) != Resp2Writer.CrLf)
            ThrowProtocolFailure();
    }
    private void SetCurrent(ReadOnlyMemory<byte> current)
    {
        _positionBase += _bufferLength; // accumulate previous length
        _bufferSpan = current.Span;
        _bufferIndex = 0;
        _bufferLength = _bufferSpan.Length;
    }
#endif

    public RespReader(ReadOnlyMemory<byte> value) : this(new ReadOnlySequence<byte>(value)) { }

    public RespReader(ReadOnlySequence<byte> value)
    {
        _positionBase = _bufferIndex = _bufferLength = 0;
#if NET7_0_OR_GREATER
        _bufferRoot = ref Unsafe.NullRef<byte>();
#else
        _bufferSpan = default;
#endif
        if (value.IsSingleSegment)
        {
            SetCurrent(value.First);
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    private static ReadOnlySpan<byte> CrLf => "\r\n"u8;

    public readonly int Length => _currentLength;
    public readonly long LongLength => Length;

    public readonly int ChildCount => Prefix switch
    {
        _ when Length <= 0 => 0, // null arrays don't have -1 child-elements; might as well handle zero here too
        RespPrefix.Array or RespPrefix.Set or RespPrefix.Push => Length,
        RespPrefix.Map /* or RespPrefix.Attribute */ => 2 * Length,
        _ => 0,
    };

    /// <summary>
    /// Indicates a type with a discreet value - string, integer, etc - <see cref="TryGetValueSpan(out ReadOnlySpan{byte})"/>,
    /// <see cref="Is(ReadOnlySpan{byte})"/>, <see cref="CopyTo(Span{byte})"/> etc are meaningful
    /// </summary>
    public readonly bool IsScalar => Prefix switch
    {
        RespPrefix.SimpleString or RespPrefix.SimpleError or RespPrefix.Integer
        or RespPrefix.Boolean or RespPrefix.Double or RespPrefix.BigNumber
        or RespPrefix.BulkError or RespPrefix.BulkString or RespPrefix.VerbatimString => true,
        _ => false,
    };

    /// <summary>
    /// Indicates a collection type - array, set, etc - <see cref="ChildCount"/>, <see cref="SkipChildren()"/> are are meaningful
    /// </summary>
    public readonly bool IsAggregate => Prefix switch
    {
        RespPrefix.Array or RespPrefix.Set or RespPrefix.Map or RespPrefix.Push => true,
        _ => false,
    };

    private static bool TryReadIntegerCrLf(ReadOnlySpan<byte> bytes, out int value, out int byteCount)
    {
        var end = bytes.IndexOf(CrLf);
        if (end < 0)
        {
            byteCount = value = 0;
            return false;
        }
        if (!(Utf8Parser.TryParse(bytes, out value, out byteCount) && byteCount == end))
            ThrowProtocolFailure();
        byteCount += 2; // include the CrLf
        return true;
    }

    private static void ThrowProtocolFailure() => throw new InvalidOperationException(); // protocol exception?

    public readonly bool IsNull()
    {
        if (_currentLength < -1) ThrowProtocolFailure();
        return _currentLength == -1;
    }

    private void ResetCurrent()
    {
        _prefix = default;
        _currentOffset = -1;
        _currentLength = 0;
    }
    public bool ReadNext()
    {
        ResetCurrent();
        if (_bufferIndex + 2 < _bufferLength) // shortest possible RESP fragment is length 3
        {
            switch (_prefix = PeekPrefix())
            {
                case RespPrefix.SimpleString:
                case RespPrefix.SimpleError:
                case RespPrefix.Integer:
                case RespPrefix.Boolean:
                case RespPrefix.Double:
                case RespPrefix.BigNumber:
                    // CRLF-terminated
                    _currentLength = PeekPastPrefix().IndexOf(CrLf);
                    if (_currentLength < 0) break;
                    _currentOffset = _bufferIndex + 1;
                    _bufferIndex += _currentLength + 3;
                    return true;
                case RespPrefix.BulkError:
                case RespPrefix.BulkString:
                case RespPrefix.VerbatimString:
                    // length prefix with value payload
                    if (!TryReadIntegerCrLf(PeekPastPrefix(), out _currentLength, out int consumed)) break;
                    _currentOffset = _bufferIndex + 1 + consumed;
                    if (IsNull())
                    {
                        _bufferIndex += consumed + 1;
                        return true;
                    }
                    if (_currentLength + 2 > (((_bufferLength - _bufferIndex) - 1) - consumed)) break;
                    AssertCrlfPastPrefixUnsafe(consumed + _currentLength);
                    _bufferIndex += consumed + _currentLength + 3;
                    return true;
                case RespPrefix.Array:
                case RespPrefix.Set:
                case RespPrefix.Map:
                case RespPrefix.Push:
                    // length prefix without value payload (child values follow)
                    if (!TryReadIntegerCrLf(PeekPastPrefix(), out _currentLength, out consumed)) break;
                    _ = IsNull(); // for validation/consistency
                    _bufferIndex += consumed + 1;
                    return true;
                case RespPrefix.Null: // null
                    // note we already checked we had 3 bytes
                    AssertCrlfPastPrefixUnsafe(0);
                    _currentOffset = _bufferIndex + 1;
                    _bufferIndex += 3;
                    return true;
                default:
                    ThrowProtocolFailure();
                    return false;
            }
        }
        return ReadSlow();
    }

    private bool ReadSlow()
    {
        ResetCurrent();
        if (_bufferLength == _bufferIndex)
        {
            // natural EOF, single chunk
            return false;
        }
        throw new NotImplementedException(); // multi-segment parsing
    }

    /// <summary>Performs a byte-wise equality check on the payload</summary>
    public readonly bool Is(ReadOnlySpan<byte> value)
    {
        if (!IsScalar) return false;
        if (TryGetValueSpan(out var span))
        {
            return span.SequenceEqual(value);
        }
        throw new NotImplementedException();
    }
}

[Experimental(RespRequest.ExperimentalDiagnosticID)]
[SuppressMessage("Usage", "CA2231:Overload operator equals on overriding value type Equals", Justification = "API not necessary here")]
public readonly struct RequestBuffer
{
    private readonly ReadOnlySequence<byte> _buffer;
    private readonly int _preambleIndex, _payloadIndex;

    public long Length => _buffer.Length - _preambleIndex;

    private RequestBuffer(ReadOnlySequence<byte> buffer, int preambleIndex, int payloadIndex)
    {
        _buffer = buffer;
        _preambleIndex = preambleIndex;
        _payloadIndex = payloadIndex;
    }

    internal RequestBuffer(ReadOnlySequence<byte> buffer, int payloadIndex)
    {
        _buffer = buffer;
        _preambleIndex = _payloadIndex = payloadIndex;
    }

    public bool TryGetSpan(out ReadOnlySpan<byte> span)
    {
        var buffer = GetBuffer(); // handle preamble
        if (buffer.IsSingleSegment)
        {
#if NETCOREAPP3_1_OR_GREATER
            span = buffer.FirstSpan;
#else
            span = buffer.First.Span;
#endif
            return true;
        }
        span = default;
        return false;
    }

    public ReadOnlySequence<byte> GetBuffer() => _preambleIndex == 0 ? _buffer : _buffer.Slice(_preambleIndex);

    /// <summary>
    /// Gets a text (UTF8) representation of the RESP payload; this API is intended for debugging purposes only, and may
    /// be misleading for non-UTF8 payloads.
    /// </summary>
    public override string ToString()
    {
        var length = Length;
        if (length == 0) return "";
        if (length > 1024) return $"({length} bytes)";
        var buffer = GetBuffer();
#if NET6_0_OR_GREATER
        return Resp2Writer.UTF8.GetString(buffer);
#else
#if NETCOREAPP3_0_OR_GREATER
        if (buffer.IsSingleSegment)
        {
            return Resp2Writer.UTF8.GetString(buffer.FirstSpan);
        }
#endif
        var arr = ArrayPool<byte>.Shared.Rent((int)length);
        buffer.CopyTo(arr);
        var s = Resp2Writer.UTF8.GetString(arr, 0, (int)length);
        ArrayPool<byte>.Shared.Return(arr);
        return s;
#endif
    }

    /// <summary>
    /// Releases all buffers associated with this instance.
    /// </summary>
    public void Recycle()
    {
        var buffer = _buffer;
        // nuke self (best effort to prevent multi-release)
        Unsafe.AsRef(in this) = default;
        new LeasedSequence<byte>(buffer).Dispose();
    }

    /// <summary>
    /// Prepends the given preamble contents 
    /// </summary>
    public RequestBuffer WithPreamble(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return this; // trivial

        int length = value.Length, preambleIndex = _preambleIndex - length;
        if (preambleIndex < 0) Throw();
        var target = _buffer.Slice(preambleIndex, length);
        if (target.IsSingleSegment)
        {
            value.CopyTo(MemoryMarshal.AsMemory(target.First).Span);
        }
        else
        {
            MultiCopy(in target, value);
        }
        return new(_buffer,  preambleIndex, _payloadIndex);

        static void Throw() => throw new InvalidOperationException("There is insufficient capacity to add the requested preamble");

        static void MultiCopy(in ReadOnlySequence<byte> buffer, ReadOnlySpan<byte> source)
        {
            // note that we've already asserted that the source is non-trivial
            var iter = buffer.GetEnumerator();
            while (iter.MoveNext())
            {
                var target = MemoryMarshal.AsMemory(iter.Current).Span;
                if (source.Length <= target.Length)
                {
                    source.CopyTo(target);
                    return;
                }
                source.Slice(0, target.Length).CopyTo(target);
                source = source.Slice(target.Length);
                Debug.Assert(!source.IsEmpty);
            }
            Debug.Assert(!source.IsEmpty);
            Throw();
            static void Throw() => throw new InvalidOperationException("Insufficient target space");
        }
    }

    /// <summary>
    /// Removes all preamble, reverting to just the original payload
    /// </summary>
    public RequestBuffer WithoutPreamble() => new RequestBuffer(_buffer, _payloadIndex, _payloadIndex);
}

[Experimental(RespRequest.ExperimentalDiagnosticID)]
public ref struct Resp2Writer
{
    private RotatingBufferCore _buffer;
    private readonly int _preambleReservation;
    private int _argCountIncludingCommand, _argIndexIncludingCommand;

    public Resp2Writer(int preambleReservation = 64, int blockSize = 1024)
    {
        _preambleReservation = preambleReservation;
        _argCountIncludingCommand = _argIndexIncludingCommand = 0;
        _buffer = new(blockSize);
        _buffer.Commit(preambleReservation);
    }

    private const int MaxBytesInt32 = 17, // $10\r\nX10X\r\n
                    MaxBytesInt64 = 26, // $19\r\nX19X\r\n
                    MaxBytesSingle = 27; // $NN\r\nX...X\r\n - note G17 format, allow 20 for payload

    private const int NullLength = 5; // $-1\r\n 

    internal void Recycle() => _buffer.Dispose();

    internal static readonly UTF8Encoding UTF8 = new(false);

    public void WriteCommand(string command, int argCount) => WriteCommand(command.AsSpan(), argCount);

    private const int MAX_UTF8_BYTES_PER_CHAR = 4, MAX_CHARS_FOR_STACKALLOC_ENCODE = 64,
        ENCODE_STACKALLOC_BYTES = MAX_CHARS_FOR_STACKALLOC_ENCODE * MAX_UTF8_BYTES_PER_CHAR;

    public void WriteCommand(scoped ReadOnlySpan<char> command, int argCount)
    {
        if (command.Length <= MAX_CHARS_FOR_STACKALLOC_ENCODE)
        {
            WriteCommand(Utf8Encode(command, stackalloc byte[ENCODE_STACKALLOC_BYTES]), argCount);
        }
        else
        {
            WriteCommandSlow(ref this, command, argCount);
        }

        static void WriteCommandSlow(ref Resp2Writer @this, scoped ReadOnlySpan<char> command, int argCount)
        {
            @this.WriteCommand(Utf8EncodeLease(command, out var lease), argCount);
            ArrayPool<byte>.Shared.Return(lease);
        }
    }

    private static unsafe ReadOnlySpan<byte> Utf8Encode(scoped ReadOnlySpan<char> source, Span<byte> target)
    {
        int len;
#if NETCOREAPP3_1_OR_GREATER
        len = UTF8.GetBytes(source, target);
#else
        fixed (byte* bPtr = target)
        fixed (char* cPtr = source)
        {
            len = UTF8.GetBytes(cPtr, source.Length, bPtr, target.Length);
        }
#endif
        return target.Slice(0, len);
    }
    private static ReadOnlySpan<byte> Utf8EncodeLease(scoped ReadOnlySpan<char> value, out byte[] arr)
    {
        arr = ArrayPool<byte>.Shared.Rent(MAX_UTF8_BYTES_PER_CHAR * value.Length);
        int len;
#if NETCOREAPP3_1_OR_GREATER
        len = UTF8.GetBytes(value, arr);
#else
        unsafe
        {
            fixed (char* cPtr = value)
            fixed (byte* bPtr = arr)
            {
                len = UTF8.GetBytes(cPtr, value.Length, bPtr, arr.Length);
            }
        }
#endif
        return new ReadOnlySpan<byte>(arr, 0, len);
    }
    internal readonly void AssertFullyWritten()
    {
        if (_argCountIncludingCommand != _argIndexIncludingCommand) Throw(_argIndexIncludingCommand, _argCountIncludingCommand);

        static void Throw(int count, int total) => throw new InvalidOperationException($"Not all command arguments ({count - 1} of {total - 1}) have been written");
    }

    public void WriteCommand(scoped ReadOnlySpan<byte> command, int argCount)
    {
        if (_argCountIncludingCommand > 0) ThrowCommandAlreadyWritten();
        if (command.IsEmpty) ThrowEmptyCommand();
        if (argCount < 0) ThrowNegativeArgs();
        _argCountIncludingCommand = argCount + 1;
        _argIndexIncludingCommand = 1;

        var payloadAndFooter = command.Length + 2;

        // optimize for single buffer-fetch path
        var worstCase = MaxBytesInt32 + MaxBytesInt32 + command.Length + 2;
        if (_buffer.TryGetWritableSpan(worstCase, out var span))
        {
            ref byte head = ref MemoryMarshal.GetReference(span);
            var header = WriteCountPrefix(RespPrefix.Array, _argCountIncludingCommand, span);
#if NETCOREAPP3_1_OR_GREATER
            header += WriteCountPrefix(RespPrefix.BulkString, command.Length,
                MemoryMarshal.CreateSpan(ref Unsafe.Add(ref head, header), MaxBytesInt32));
            command.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref head, header), command.Length));
#else
            header += WriteCountPrefix(RespPrefix.BulkString, command.Length, span.Slice(header));
            command.CopyTo(span.Slice(header));
#endif

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, header + command.Length), CrLf);
            _buffer.Commit(header + command.Length + 2);
            return; // yay!
        }

        // slow path, multiple buffer fetches
        WriteCountPrefix(RespPrefix.Array, _argCountIncludingCommand);
        WriteCountPrefix(RespPrefix.BulkString, command.Length);
        WriteRaw(command);
        WriteRaw(CrlfBytes);


        static void ThrowCommandAlreadyWritten() => throw new InvalidOperationException(nameof(WriteCommand) + " can only be called once");
        static void ThrowEmptyCommand() => throw new ArgumentOutOfRangeException(nameof(command), "command cannot be empty");
        static void ThrowNegativeArgs() => throw new ArgumentOutOfRangeException(nameof(argCount), "argCount cannot be negative");
    }

    private static int WriteCountPrefix(RespPrefix prefix, int count, Span<byte> target)
    {
        var len = Format.FormatInt32(count, target.Slice(1)); // we only want to pay for this one slice
        if (target.Length < len + 3) Throw();
        ref byte head = ref MemoryMarshal.GetReference(target);
        head = (byte)prefix;
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, len + 1), CrLf);
        return len + 3;

        static void Throw() => throw new InvalidOperationException("Insufficient buffer space to write count prefix");
    }

    private void WriteNullString() // private because I don't think this is allowed in client streams? check
        => WriteRaw("$-1\r\n"u8);

    private void WriteEmptyString() // private because I don't think this is allowed in client streams? check
        => WriteRaw("$0\r\n\r\n"u8);

    private void WriteRaw(scoped ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty)
        {
            var target = _buffer.GetWritableTail().Span;
            Debug.Assert(!target.IsEmpty, "need something!");

            if (target.Length >= value.Length)
            {
                // it all fits
                value.CopyTo(target);
                _buffer.Commit(value.Length);
                return;
            }
            
            // write what we can
            value.Slice(target.Length).CopyTo(target);
            _buffer.Commit(target.Length);
            value = value.Slice(target.Length);
        }
    }

    private void AddArg()
    {
        if (_argIndexIncludingCommand >= _argCountIncludingCommand) ThrowAllWritten(_argCountIncludingCommand);
        _argIndexIncludingCommand++;

        static void ThrowAllWritten(int advertised) => throw new InvalidOperationException($"All command arguments ({advertised - 1}) have already been written");
    }

    public void WriteValue(scoped ReadOnlySpan<byte> value)
    {
        AddArg();
        if (value.IsEmpty)
        {
            WriteEmptyString();
            return;
        }
        // optimize for fitting everything into a single buffer-fetch
        var payloadAndFooter = value.Length + 2;
        var worstCase = MaxBytesInt32 + payloadAndFooter;
        if (_buffer.TryGetWritableSpan(worstCase, out var span))
        {
            ref byte head = ref MemoryMarshal.GetReference(span);
            var header = WriteCountPrefix(RespPrefix.BulkString, value.Length, span);
#if NETCOREAPP3_1_OR_GREATER
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref head, header), payloadAndFooter));
#else
            value.CopyTo(span.Slice(header));
#endif
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, header + value.Length), CrLf);
            _buffer.Commit(header + payloadAndFooter);
            return; // yay!
        }

        // slow path - involves multiple buffer fetches
        WriteCountPrefix(RespPrefix.BulkString, value.Length);
        WriteRaw(value);
        WriteRaw(CrlfBytes);
    }

    private void WriteCountPrefix(RespPrefix prefix, int count)
    {
        Span<byte> buffer = stackalloc byte[MaxBytesInt32];
        WriteRaw(buffer.Slice(0, WriteCountPrefix(prefix, count, buffer)));
    }

    internal static readonly ushort CrLf = BitConverter.IsLittleEndian ? (ushort)0x0A0D : (ushort)0x0D0A;

    internal static ReadOnlySpan<byte> CrlfBytes => "\r\n"u8;

    public void WriteValue(scoped ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            AddArg();
            WriteEmptyString();
        }
        else if (value.Length <= MAX_CHARS_FOR_STACKALLOC_ENCODE)
        {
            WriteValue(Utf8Encode(value, stackalloc byte[ENCODE_STACKALLOC_BYTES]));
        }
        else
        {
            WriteValue(Utf8EncodeLease(value, out var lease));
            ArrayPool<byte>.Shared.Return(lease);
        }
    }

    public void WriteValue(string value)
    {
        if (value is null)
        {
            AddArg();
            WriteNullString();
        }
        else WriteValue(value.AsSpan());
    }

    internal RequestBuffer Detach() => new RequestBuffer(_buffer.Detach(), _preambleReservation);
}

internal struct RotatingBufferCore : IDisposable, IBufferWriter<byte> // note mutable struct intended to encapsulate logic as a field inside a class instance
{
    private RefCountedSequenceSegment<byte> _head, _tail;
    private readonly long _maxLength;
    private readonly int _xorBlockSize;
    private int _headOffset, _tailOffset, _tailSize;
    internal readonly int BlockSize => _xorBlockSize ^ DEFAULT_BLOCK_SIZE; // allows default to apply on new()
    internal readonly long MaxLength => _maxLength;

    private const int DEFAULT_BLOCK_SIZE = 1024;

    public RotatingBufferCore(int blockSize, int maxLength = 0)
    {
        if (maxLength <= 0) maxLength = int.MaxValue;
        _xorBlockSize = blockSize ^ DEFAULT_BLOCK_SIZE;
        _maxLength = maxLength;
        _headOffset = _tailOffset = _tailSize = 0;
        Expand();
    }

    /// <summary>
    /// The immediately available contiguous bytes in the current buffer (or next buffer, if none)
    /// </summary>
    public readonly int AvailableBytes
    {
        get
        {
            var remaining = _tailSize - _tailOffset;
            return remaining == 0 ? BlockSize : remaining;
        }
    }

    [MemberNotNull(nameof(_head))]
    [MemberNotNull(nameof(_tail))]
    private void Expand()
    {
        Debug.Assert(_tail is null || _tailOffset == _tail.Memory.Length, "tail page should be full");
        if (MaxLength > 0 && (GetBuffer().Length + BlockSize) > MaxLength) ThrowQuota();
        var next = new RefCountedSequenceSegment<byte>(BlockSize, _tail);
        _tail = next;
        _tailOffset = 0;
        _tailSize = next.Memory.Length;
        if (_head is null)
        {
            _head = next;
            _headOffset = 0;
        }

        static void ThrowQuota() => throw new InvalidOperationException("Buffer quota exceeded");
    }

    public bool TryGetWritableSpan(int minSize, out Span<byte> span)
    {
        if (minSize <= AvailableBytes) // don't pay lookup cost if impossible
        {
            span = GetWritableTail().Span;
            return span.Length >= minSize; 
        }
        span = default;
        return false;
    }

    public Memory<byte> GetWritableTail()
    {
        if (_tailOffset == _tailSize)
        {
            Expand();
        }
        // definitely something available; return the gap
        return MemoryMarshal.AsMemory(_tail.Memory).Slice(_tailOffset);
    }
    public readonly ReadOnlySequence<byte> GetBuffer() => _head is null ? default : new(_head, _headOffset, _tail, _tailOffset);
    internal void Commit(int bytes) // unlike Advance, this remains valid for data outside what has been written
    {
        if (bytes >= 0 && bytes <= _tailSize - _tailOffset)
        {
            _tailOffset += bytes;
        }
        else
        {
            CommitSlow(bytes);
        }
    }
    private void CommitSlow(int bytes) // multi-segment commits (valid even though it remains unwritten) and error-cases
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        while (bytes > 0)
        {
            var space = _tailSize - _tailOffset;
            if (bytes <= space)
            {
                _tailOffset += bytes;
            }
            else
            {
                _tailOffset += space;
                Expand(); // need more
            }
            bytes -= space;
        }
    }

    /// <summary>
    /// Detaches the entire committed chain to the caller without leaving things in a resumable state
    /// </summary>
    public ReadOnlySequence<byte> Detach()
    {
        var all = GetBuffer();
        _head = _tail = null!;
        _headOffset = _tailOffset = _tailSize = 0;
        return all;
    }

    /// <summary>
    /// Detaches the head portion of the committed chain, retaining the rest of the buffered data
    /// for additional use
    /// </summary>
    public ReadOnlySequence<byte> DetachRotating(long bytes)
    {
        // semantically, we're going to AddRef on all the nodes in take, and then
        // drop (and Dispose()) all nodes that we no longer need; but this means
        // that the only shared segment is the first one (and only if there is data left),
        // so we can manually check that one segment, rather than walk two chains
        var all = GetBuffer();
        var take = all.Slice(0, bytes);

        var end = take.End;
        var endSegment = (RefCountedSequenceSegment<byte>)end.GetObject()!;

        var bytesLeftLastPage = endSegment.Memory.Length - end.GetInteger();
        if (bytesLeftLastPage != 0 && (
            bytesLeftLastPage >= 64 // worth using for the next read, regardless
            || endSegment.Next is not null // we've already allocated another page, which means this page is full
            || _tailOffset != end.GetInteger() // (^^ final page) & we have additional read bytes
            ))
        {
            // keep sharing the last page of the outbound / first page of retained
            endSegment.AddRef();
            _head = endSegment;
            _headOffset = end.GetInteger();
        }
        else
        {
            // move to the next page
            _headOffset = 0;
            if (endSegment.Next is null)
            {
                // no next page buffered; reset completely
                Debug.Assert(ReferenceEquals(endSegment, _tail));
                _head = _tail = null!;
                Expand();
            }
            else
            {
                // start fresh from the next page
                var next = endSegment.Next;
                endSegment.Next = null; // walk never needed
                _head = next;
            }
        }
        return take;
    }

    public void Dispose()
    {
        LeasedSequence<byte> leased = new(GetBuffer());
        _head = _tail = null!;
        _headOffset = _tailOffset = _tailSize = 0;
        leased.Dispose();
    }

    void IBufferWriter<byte>.Advance(int count) => Commit(count);
    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint) => GetWritableTail();
    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint) => GetWritableTail().Span;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SPDY
{
	#region SPDYStream
	/// <summary>Represents a stream created by a <see cref="SPDYClient"/>.</summary>
	public sealed class SPDYStream : Stream
	{
		internal SPDYStream(SPDYClient client, int id, int associatedId, bool canRead, bool canWrite, bool open, IDictionary<string,List<string>> headers = null)
		{
			if(!canRead && !canWrite) throw new ArgumentException("The stream is neither readable nor writable.");
			Client = client;
			Id = id;
			AssociatedId = associatedId;
			_canRead = canRead;
			_canWrite = canWrite;
			if(headers != null) AddHeadersCore(headers);
			if(!canRead) readPipe.Finish();
			if(open) SetState(SPDYStreamState.Open);
		}

		/// <summary>Gets the ID of the stream that this stream is associated with, or 0 if this stream is independent of other streams.</summary>
		public int AssociatedId { get; private set; }

		/// <inheritdoc/>
		public override bool CanRead => _canRead;

		/// <inheritdoc/>
		public override bool CanSeek => false;

		/// <inheritdoc/>
		public override bool CanWrite => _canWrite;

		/// <summary>Gets the <see cref="SPDYClient"/> that created this stream.</summary>
		public SPDYClient Client { get; private set; }

		/// <summary>Gets the stream's unique ID in the SPDY protocol.</summary>
		public int Id { get; private set; }

		/// <inheritdoc/>
		/// <remarks>This property is not supported and throws <see cref="NotSupportedException"/>.</remarks>
		public override long Length => throw new NotSupportedException();

		/// <inheritdoc/>
		/// <remarks>This property is not supported and throws <see cref="NotSupportedException"/>.</remarks>
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		/// <summary>Gets the reason why the stream was reset, or <see cref="SPDYResetReason.NotReset"/> if the stream was not reset.</summary>
		public SPDYResetReason ResetReason { get; private set; }

		/// <summary>Gets the current <see cref="SPDYStreamState"/> of the stream.</summary>
		public SPDYStreamState State { get; private set; }

		/// <summary>Augments the stream with additional headers.</summary>
		/// <remarks>The new headers will be communicated to the remote side of the connection.</remarks>
		public void AddHeaders(IDictionary<string, List<string>> headers)
		{
			AssertNotDisposed();
			lock(lockObj)
			{
				AssertWritable();
				if(headers != null && headers.Count != 0)
				{
					AddHeadersCore(headers);
					Client.OnHeadersAdded(this, headers);
				}
			}
		}

		/// <summary>Marks the stream as finished, meaning that no more data will be written to the stream and no more headers will be
		/// changed.
		/// </summary>
		public void Finish()
		{
			AssertNotDisposed();
			if(!CanWrite) throw new InvalidOperationException("The stream is not writable.");
			lock(lockObj)
			{
				if(!WasFinishSent)
				{
					SetState(SPDYStreamState.FinishSent);
					Client.FinishStream(this);
				}
			}
		}

		/// <inheritdoc/>
		public override void Flush() => AssertNotDisposed(); // TODO: should we make Flush wait until data has been sent over the wire?

		/// <summary>Gets a copy of the headers associated with the stream.</summary>
		public string[] GetHeader(string key)
		{
			lock(lockObj)
			{
				if(headers != null && headers.TryGetValue(key, out List<string> L) && L.Count != 0) return L.ToArray();
			}
			return null;
		}

		/// <summary>Gets a copy of the headers associated with the stream.</summary>
		public Dictionary<string,List<string>> GetHeaders()
		{
			lock(lockObj)
			{
				if(headers == null) return new Dictionary<string, List<string>>();
				var copy = new Dictionary<string, List<string>>(headers.Count);
				foreach(KeyValuePair<string, List<string>> pair in headers) copy[pair.Key] = new List<string>(pair.Value);
				return copy;
			}
		}

#if !NETSTANDARD2_0 && !NET45 && !NET46
		/// <inheritdoc/>
		public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

		/// <inheritdoc/>
		public override int Read(Span<byte> buffer)
		{
			AssertReadable();
			int read = readPipe.Read(buffer);
			if(read != 0) Client.OnDataConsumed(this, read);
			return read;
		}

		/// <inheritdoc/>
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancelToken) =>
			ReadAsync(new Memory<byte>(buffer, offset, count), cancelToken).AsTask();

		/// <inheritdoc/>
		public async override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancelToken = default)
		{
			AssertReadable();
			int read = await readPipe.ReadAsync(buffer, cancelToken).ConfigureAwait(false);
			if(read != 0) Client.OnDataConsumed(this, read);
			return read;
		}
#else
		/// <inheritdoc/>
		public override int Read(byte[] buffer, int offset, int count)
		{
			AssertReadable();
			int read = readPipe.Read(buffer, offset, count);
			if(read != 0) Client.OnDataConsumed(this, read);
			return read;
		}

		/// <inheritdoc/>
		public async override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancelToken)
		{
			AssertReadable();
			int read = await readPipe.ReadAsync(buffer, offset, count, cancelToken).ConfigureAwait(false);
			if(read != 0) Client.OnDataConsumed(this, read);
			return read;
		}
#endif

		/// <inheritdoc/>
		/// <remarks>This method is not supported and throws <see cref="NotSupportedException"/>.</remarks>
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		/// <inheritdoc/>
		/// <remarks>This method is not supported and throws <see cref="NotSupportedException"/>.</remarks>
		public override void SetLength(long value) => throw new NotSupportedException();

#if !NETSTANDARD2_0 && !NET45 && !NET46
		/// <inheritdoc/>
		public override void Write(byte[] buffer, int offset, int count) => Write(new ReadOnlySpan<byte>(buffer, offset, count));

		/// <inheritdoc/>
		public override void Write(ReadOnlySpan<byte> buffer)
		{
			AssertWritable();
			// TODO: writes should block (outside the lock) if too much data has been queued for the stream, and we should have an async version
			lock(lockObj) Client.QueueData(this, buffer);
		}
#else
		/// <inheritdoc/>
		public override void Write(byte[] buffer, int offset, int count)
		{
			AssertWritable();
			// TODO: writes should block (outside the lock) if too much data has been queued for the stream, and we should have an async version
			lock(lockObj) Client.QueueData(this, new ReadOnlySpan<byte>(buffer, offset, count));
		}
#endif

		/// <inheritdoc/>
		/// <remarks>This method puts the stream into a <see cref="SPDYStreamState.Closing"/> state, and it will be finally closed by the
		/// <see cref="SPDYClient"/> when all related data has been sent, etc.
		/// </remarks>
		protected override void Dispose(bool manuallyDisposed)
		{
			if(!disposed)
			{
				readPipe.Dispose();
				bool wasFinished = WasFinishSent;
				SetState(SPDYStreamState.Closing); // set the state before FinishStream so the writer is sure to see us in a closing state
				if(!wasFinished && CanWrite)
				{
					try { Client.FinishStream(this); }
					catch(InvalidOperationException) { } // ignore errors that may occur if the stream was just reset
				}
				disposed = true;
			}
		}

		internal int DataAvailable => readPipe.DataAvailable;

		/// <summary>Gets whether the remote side is not supposed to send any more frames related to this stream.</summary>
		internal bool WasFinishReceived => State >= SPDYStreamState.FinishReceived && State != SPDYStreamState.FinishSent;

		/// <summary>Gets whether we are not supposed to send any more frames related to this stream.</summary>
		internal bool WasFinishSent => State >= SPDYStreamState.FinishSent;

		/// <summary>Merges the given headers into our copy of the headers data structure.</summary>
		internal void AddHeadersCore(IDictionary<string, List<string>> headers)
		{
			if(headers != null && headers.Count != 0)
			{
				lock(lockObj)
				{
					if(this.headers == null) this.headers = new Dictionary<string, List<string>>();
					foreach(KeyValuePair<string, List<string>> pair in headers) this.headers[pair.Key] = new List<string>(pair.Value);
				}
			}
		}

		/// <summary>Called when the stream has been reset.</summary>
		internal void OnReset(SPDYResetReason reason)
		{
			lock(lockObj)
			{
				ResetReason = reason;
				State = SPDYStreamState.Closed;
				if(!disposed) readPipe.Finish();
			}
		}

		/// <summary>Queues data that can later be read from the stream.</summary>
		internal void QueueData(byte[] data, int count) => readPipe.Write(data, 0, count);

		/// <summary>Attempts to transition the stream into the given state.</summary>
		internal void SetState(SPDYStreamState newState)
		{
			lock(lockObj)
			{
				if(State == SPDYStreamState.HalfOpen && newState != SPDYStreamState.HalfOpen)
				{
					if(!CanRead) State = SPDYStreamState.FinishReceived;
					else if(!CanWrite) State = SPDYStreamState.FinishSent;
				}
				if(newState > State)
				{
					State = newState == SPDYStreamState.FinishSent && State == SPDYStreamState.FinishReceived ?
						SPDYStreamState.Closing : newState;
				}
				else if(newState == SPDYStreamState.FinishReceived && State == SPDYStreamState.FinishSent)
				{
					State = SPDYStreamState.Closing;
				}
			}
		}

		void AssertNotDisposed()
		{
			if(disposed) throw new ObjectDisposedException(GetType().Name);
		}

		void AssertReadable()
		{
			AssertNotDisposed();
			if(!CanRead) throw new InvalidOperationException("The stream is not readable.");
		}

		void AssertWritable()
		{
			AssertNotDisposed();
			if(!CanWrite) throw new InvalidOperationException("The stream is not writable.");
			if(State >= SPDYStreamState.Closing) throw new InvalidOperationException("The stream is closed.");
			if(State == SPDYStreamState.FinishSent) throw new InvalidOperationException("The stream has been finished.");
		}

		readonly object lockObj = new object();
		readonly bool _canRead, _canWrite;
		readonly Pipe readPipe = new Pipe();
		Dictionary<string, List<string>> headers;
		bool disposed;
	}
	#endregion

	#region SPDYStreamState
	/// <summary>Describes the current state of a <see cref="SPDYStream"/>.</summary>
	public enum SPDYStreamState
	{
		/// <summary>The stream has been created locally but not yet accepted by the remote side.</summary>
		HalfOpen = 0,
		/// <summary>The stream has been accepted and is readable and writable by both sides.</summary>
		Open = 1,
		/// <summary>The stream is open but the remote side will not be sending any more data.</summary>
		FinishReceived = 2,
		/// <summary>The stream is open but the local side will not be sending any more data.</summary>
		FinishSent = 3,
		/// <summary>The stream is open (in the SPDY protocol) but neither side will be sending any more data. Data can be read from the
		/// stream if it hasn't been disposed.
		/// </summary>
		Closing = 4,
		/// <summary>The stream has been closed (in the SPDY protocol). Data can still be read from it if it hasn't been disposed.</summary>
		Closed = 5
	}
	#endregion
}

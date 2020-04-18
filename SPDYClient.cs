using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// TODO: use more fine-grained locking (especially for streams)

namespace SPDY
{
	/// <summary>Implements a SPDY client (or server).</summary>
	public sealed class SPDYClient : IDisposable
	{
		/// <summary>Initializes a new <see cref="SPDYClient"/> based on an established <see cref="SPDYConnection"/>.</summary>
		/// <param name="connection">An open <see cref="SPDYConnection"/></param>
		/// <param name="isClient">If true, this instance is considered the client in the connection. Otherwise, it's considered to be
		/// the server. The default is true.
		/// </param>
		/// <param name="streamWindowSize">The receive window size for each stream, in bytes. The default is 256KB.</param>
		/// <param name="globalWindowSize">The receive window size for the entire connection, in bytes. The default is 1MB.</param>
		public SPDYClient(SPDYConnection connection, bool isClient = true, int streamWindowSize = 256*1024, int globalWindowSize = 1024*1024)
		{
			this.conn = connection ?? throw new ArgumentNullException();
			if(streamWindowSize <= 0) throw new ArgumentOutOfRangeException(nameof(streamWindowSize));
			if(globalWindowSize <= 0) throw new ArgumentOutOfRangeException(nameof(globalWindowSize));
			defaultInWindowSize = Math.Max(streamWindowSize, 8192);
			inWindowSize = Math.Max(globalWindowSize, inWindowSpace);
			inWindowPending = inWindowSize - inWindowSpace;
			this.isClient = isClient;
			if(!isClient) nextStreamId++; // servers must use even IDs
		}

		/// <summary>Raised when a stream is opened, either after a stream created with <see cref="CreateStream"/> is accepted by the
		/// other side, or after a stream created by the other side was accepted by this side.
		/// </summary>
		/// <remarks>The event handler must execute quickly and must not throw an exception.</remarks>
		public event Action<SPDYStream> StreamOpened;

		/// <summary>Raised when a stream is closed. This event is not raised when all streams are implicitly closed due to the closure
		/// of the entire connection.
		/// </summary>
		/// <remarks>The event handler must execute quickly and must not throw an exception.</remarks>
		public event Action<SPDYStream> StreamReset;

		/// <summary>Raised when a stream's metadata is updated due to the other side augmenting it with additional header values.</summary>
		/// <remarks>The event handler must execute quickly and must not throw an exception.</remarks>
		public event Action<SPDYStream> StreamUpdated;

		/// <summary>Gets whether the connection is open and the client <see cref="State"/> is not <see cref="SPDYClientState.Closed"/>.
		/// Like <see cref="SPDYConnection.IsOpen"/>, this property may not be updated until the next attempt to read or write.
		/// </summary>
		public bool IsOpen => conn.IsOpen && State != SPDYClientState.Closed;

		/// <summary>Gets or sets a function that determines whether a proposed stream should be accepted.</summary>
		/// <remarks>The function must execute quickly and must not throw an exception.</remarks>
		public Func<SPDYStream, bool> ShouldAccept { get; set; }

		/// <summary>Gets the current <see cref="SPDYClientState"/> of the client.</summary>
		public SPDYClientState State { get; private set; }

		/// <summary>Gets or sets whether flow control should be used. Flow control is mandated by the SPDY standard, but not all
		/// implementations support it. (If one side uses flow control and the other doesn't, communication will likely fail.)
		/// The default is true.
		/// </summary>
		public bool UseFlowControl
		{
			get => _useFlowControl;
			set
			{
				if(State != SPDYClientState.NotStarted) throw new InvalidOperationException("The client has already been started.");
				_useFlowControl = value;
			}
		}

		/// <summary>Proposes the creation of a new stream.</summary>
		/// <param name="canRead">If true, the stream can be read by this side and written by the remote side. If false, the stream
		/// is write-only. The default is true.
		/// </param>
		/// <param name="canWrite">If true, the stream can be written by this side and read by the remote side. If false, the stream
		/// is read-only. The default is true.
		/// </param>
		/// <param name="associatedStreamId">The ID of another stream that the new stream will be associated with, or zero if the new
		/// stream is independent. The default is zero.</param>
		/// <param name="priority">The priority of the stream from 0 (highest) to 7 (lowest). The default is 3.</param>
		/// <param name="headers">An set of headers to associate with the stream, or null or empty to not provide any headers.
		/// The default is null.
		/// </param>
		/// <returns>Returns the new <see cref="SPDYStream"/>. If you're unsure whether the stream will be accepted, you should wait for
		/// the corresponding <see cref="StreamOpened"/> event to be raised before writing to it, but if you know the stream will be
		/// accepted you can begin writing to it immediately.
		/// </returns>
		public SPDYStream CreateStream(bool canRead = true, bool canWrite = true,
		                               int associatedStreamId = 0, int priority = 3, IDictionary<string, List<string>> headers = null)
		{
			if(!(canRead | canWrite)) throw new ArgumentException("The stream is neither readable nor writable.");
			if(associatedStreamId < 0) throw new ArgumentOutOfRangeException(nameof(associatedStreamId));
			if((uint)priority > 7) throw new ArgumentOutOfRangeException(nameof(priority));
			if(WasGoAwayReceived) throw new InvalidOperationException($"The client is in the {State.ToString()} state.");
			lock(streams)
			{
				if(nextStreamId < 0) throw new InvalidOperationException("Too many streams have been created.");
				int streamId = nextStreamId;
				nextStreamId += 2;
				EnqueueControlFrame(
					SPDYFrame.NewStream(streamId, conn.CompressHeaders(headers), !canRead, !canWrite, associatedStreamId, priority));
				var stream = new SPDYStream(this, streamId, associatedStreamId, canRead, canWrite, false, headers);
				var meta = new StreamMeta(stream, priority, false, defaultInWindowSize, defaultOutWindowSize);
				streams.Add(streamId, meta);
				if(canWrite) AddStreamToWriteList(meta);
				return stream;
			}
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			writeCts?.Cancel();
			conn.Dispose();
			OnConnectionClosed();
			lock(streams)
			{
				foreach(StreamMeta meta in streams.Values) meta.Dispose();
				streams.Clear();
				writableStreamList.Clear();
			}
		}

		/// <summary>Gets the stream with the given ID, or returns null if no such stream exists.</summary>
		public SPDYStream GetStream(int streamId)
		{
			SPDYStream stream = null;
			lock(streams)
			{
				if(streams.TryGetValue(streamId, out StreamMeta meta)) stream = meta.Stream;
			}
			return stream;
		}

		/// <summary>Runs the client. This method may only be called once.</summary>
		/// <param name="shutdownToken">A <see cref="CancellationToken"/> that can be used to shut down the client gracefully</param>
		/// <param name="cancelToken">A <see cref="CancellationToken"/> that can be used to shut down the client abruptly</param>
		/// <remarks>To effect a graceful shutdown, cancel the <paramref name="shutdownToken"/> and close open streams as soon as you're
		/// done with them.
		/// </remarks>
		public async Task RunAsync(CancellationToken shutdownToken = default, CancellationToken cancelToken = default)
		{
			if(State != SPDYClientState.NotStarted) throw new InvalidOperationException("The client was already started.");
			if(!conn.IsOpen) throw new InvalidOperationException("The connection has been closed.");
			State = SPDYClientState.Open;

			// if a shutdown token was passed, register a callback that will begin the graceful shutdown process
			var shutdownReg = default(CancellationTokenRegistration);
			if(shutdownToken.CanBeCanceled)
			{
#if NETCOREAPP3_0
				shutdownReg = shutdownToken.UnsafeRegister(_ =>
#else
				shutdownReg = shutdownToken.Register(_ =>
#endif
				{
					if(!WasGoAwaySent) // if a GOAWAY frame wasn't already sent...
					{
						GoAway(); // send one
					}
					else if(State < SPDYClientState.Closing) // otherwise, if one was sent but we didn't tell the write task to exit...
					{
						SetState(SPDYClientState.Closing); // let the write task know that it should exit when all streams are closed
						writeEvent.Set(); // and wake it up if it's sleeping
					}
				}, null);
			}
			using(shutdownReg)
			{
				if(UseFlowControl) // if flow control is enabled, communicate our initial window settings
				{
					if(defaultInWindowSize != defaultOutWindowSize) // send our default stream window size if different from the SPDY default
					{
						EnqueueControlFrame(SPDYFrame.Settings(false,
							new SPDYSetting(SPDYSettingId.InitialWindowSize, (uint)defaultInWindowSize)));
					}
					if(inWindowPending != 0) // inform the other side that we have additional space in our global window, if in fact we do
					{
						EnqueueControlFrame(SPDYFrame.Window(0, inWindowPending));
						inWindowSpace += inWindowPending;
						inWindowPending = 0;
					}
				}

				writeCts = cancelToken.CanBeCanceled ? // start a background task to write outgoing data
					CancellationTokenSource.CreateLinkedTokenSource(cancelToken) : new CancellationTokenSource();
				Task writeTask = WriteData(writeCts.Token);
				while(!cancelToken.IsCancellationRequested) // while we're not supposed to exit...
				{
					try
					{
						SPDYFrame frame = await conn.ReceiveFrameAsync(cancelToken).ConfigureAwait(false); // read the next frame
						if(!frame.IsValid) { break; } // if the connection was closed and we've read all the data, we're done
						if(!frame.IsControlFrame) // if it's a data frame...
						{
							HandleStreamData(frame); // enqueue the data
						}
						else // otherwise, it's a control frame
						{
							switch(frame.Type) // handle each of the control frame types
							{
								case SPDYFrameType.GoAway: HandleGoAway(frame); break;
								case SPDYFrameType.Headers: UpdateStreamHeaders(frame); break;
								case SPDYFrameType.Ping: EnqueueControlFrame(SPDYFrame.Ping(frame.ReadUint(0)), true); break;
								case SPDYFrameType.Reply: HandleStreamAccepted(frame); break;
								case SPDYFrameType.Reset: HandleStreamReset(frame); break;
								case SPDYFrameType.Settings: HandleSettings(frame); break;
								case SPDYFrameType.Stream: HandleNewStream(frame); break;
								case SPDYFrameType.Window: UpdateWindowSpace(frame); break;
								default: OnProtocolError(); break;
							}
						}
						frame.Dispose(); // either way, dispose the frame to return its memory to the buffer pool
					}
					catch(OperationCanceledException) { break; } // if we're supposed to shut down...
					catch(InvalidOperationException) { break; } // if the connection was closed...
					catch(EndOfStreamException) { break; } // if the connection was closed abruptly...
					catch // if an unexpected error occurred
					{
						Dispose(); // then close everything down
						throw; // and rethrow it
					}
				}

				writeCts.Cancel(); // on a graceful shutdown, the WriteTask should have stopped already. otherwise, force it
				await writeTask.ConfigureAwait(false); // and wait for the write task to finish writing
				OnConnectionClosed(); // then mark the connection closed
			}
		}

		/// <summary>Sends a <see cref="SPDYFrameType.GoAway"/> frame to the remote side, telling it not to create any more streams.
		/// This may be interpreted as a request to begin closing the connection.
		/// </summary>
		public void GoAway()
		{
			if(State == SPDYClientState.NotStarted) throw new InvalidOperationException("The client has not been started.");
			if(!WasGoAwaySent) GoAway(SPDYShutdownReason.Normal, false, false);
		}

		/// <summary>Called when a writable <see cref="SPDYStream"/> has been closed, this method enqueues the final frame for the
		/// stream.
		/// </summary>
		internal void FinishStream(SPDYStream stream) => QueueData(stream, Span<byte>.Empty, true);

		/// <summary>Called when data has been read from a <see cref="SPDYStream"/>, this method updates the flow control windows and
		/// closes the stream if .</summary>
		internal void OnDataConsumed(SPDYStream stream, int length)
		{
			lock(streams)
			{
				if(streams.TryGetValue(stream.Id, out StreamMeta meta))
				{
					if(UseFlowControl)
					{
						inWindowPending += length; // ditto for the global flow control window
						if(inWindowPending >= inWindowSpace/2)
						{
							EnqueueControlFrame(SPDYFrame.Window(0, inWindowPending));
							inWindowSpace += inWindowPending;
							inWindowPending = 0;
						}
					}

					CloseStreamIfComplete(meta);
					if(UseFlowControl)
					{
						meta.inWindowPending += length; // record how much data has been consumed but not yet communicated
						// if it reaches half the window size, let the other side know
						if(stream.State != SPDYStreamState.Closed && meta.inWindowPending >= meta.inWindowSize/2)
						{
							EnqueueControlFrame(SPDYFrame.Window(stream.Id, meta.inWindowPending));
							meta.inWindowSpace += meta.inWindowPending; // then reset the window to its full size
							meta.inWindowPending = 0;
						}
					}
				}
			}
		}

		/// <summary>Called when headers have been added to a <see cref="SPDYStream"/> via
		/// <see cref="SPDYStream.AddHeaders(IDictionary{string, List{string}})"/>, this method notifies the other side of the new
		/// headers.
		/// </summary>
		internal void OnHeadersAdded(SPDYStream stream, IDictionary<string, List<string>> headers)
		{
			lock(streams)
			{
				if(streams.ContainsKey(stream.Id)) EnqueueControlFrame(SPDYFrame.Headers(stream.Id, conn.CompressHeaders(headers)));
			}
		}

		/// <summary>Called when data is written to a <see cref="SPDYStream"/>, this method enqueues the data to be sent over the network.</summary>
		internal void QueueData(SPDYStream stream, ReadOnlySpan<byte> data) => QueueData(stream, data, false);

		#region StreamMeta
		/// <summary>Holds metadata about a <see cref="SPDYStream"/>.</summary>
		sealed class StreamMeta : IDisposable
		{
			public StreamMeta(SPDYStream stream, int priority, bool accepted, int inWindowSize, int outWindowSize)
			{
				Stream = stream;
				Priority = (byte)priority;
				Accepted = accepted;
				Finalized = !stream.CanWrite;
				SentFinalFrame = Finalized; // if the stream was created in a finalized state, no final frame needs to be sent
				if(!Finalized) OutputBuffer = new StreamBuffer(); // if the stream is writable, create its output buffer
				inWindowSpace = this.inWindowSize = inWindowSize; // the stream starts out with the full window space available
				outWindowSpace = outWindowSize;
			}

			public void Dispose()
			{
				Stream.Dispose();
				OutputBuffer?.Dispose();
			}

			/// <summary>Called to enqueue data in the stream's <see cref="OutputBuffer"/> and optionally mark the stream as finalized.</summary>
			public void Enqueue(ReadOnlySpan<byte> data, bool final)
			{
				if(Finalized) throw new InvalidOperationException("The stream has been finalized.");
				OutputBuffer.Write(data);
				if(final) Finalized = true;
			}

			/// <summary>Called when the <see cref="SPDYConnection"/> has been closed.</summary>
			public void OnConnectionClosed()
			{
				Stream.SetState(SPDYStreamState.Closed);
				OutputBuffer?.Dispose();
			}

			public readonly SPDYStream Stream; // the stream itself
			public readonly StreamBuffer OutputBuffer; // the output buffer, if it's writable
			public int inWindowPending, inWindowSize, inWindowSpace, outWindowSpace; // the flow control window data
			public readonly byte Priority; // the stream's priority - 0 is highest and 7 is lowest
			public bool Accepted; // whether the stream has been accepted
			public bool Finalized, SentFinalFrame; // whether the stream has been finalized, and whether that has been communicated
		}
		#endregion

		/// <summary>Gets whether we've effectively received a <see cref="SPDYFrameType.GoAway"/> frame, i.e. whether we should not
		/// create any new streams.
		/// </summary>
		bool WasGoAwayReceived => State >= SPDYClientState.GoAwayReceived && State != SPDYClientState.GoAwaySent;

		/// <summary>Gets whether we've effectively sent a <see cref="SPDYFrameType.GoAway"/> frame, i.e. whether we should ignore any
		/// new stream requests.
		/// </summary>
		bool WasGoAwaySent => State >= SPDYClientState.GoAwaySent;

		/// <summary>Adds a writable stream to a suitable location within the writable stream list, which is kept ordered by priority.</summary>
		void AddStreamToWriteList(StreamMeta meta) // NOTE: this method must be called with the streams lock held
		{
			LinkedListNode<StreamMeta> node = writableStreamList.First; // find the first node that's of lesser or equal priority
			while(node != null && meta.Priority > node.Value.Priority) node = node.Next; // zero is highest priority, so it's reversed
			if(node == null) writableStreamList.AddLast(meta); // if there's no lower/equal-priority node, put the new stream at the end
			else writableStreamList.AddBefore(node, meta); // otherwise, put it before the possibly lower-priority node
		}

		/// <summary>Disposes and removes all frames from the control frame queue.</summary>
		void ClearControlQueue()
		{
			lock(controlQueue)
			{
				foreach(SPDYFrame frame in controlQueue) frame.Dispose();
				controlQueue.Clear();
			}
		}

		/// <summary>Closes the stream if nothing more can be done with it.</summary>
		void CloseStreamIfComplete(StreamMeta meta)
		{
			if(meta.Stream.State == SPDYStreamState.Closing && meta.Stream.DataAvailable == 0 && meta.SentFinalFrame && meta.Accepted)
			{
				// we reset it even if we didn't create it. not 100% kosher, but keeps things simple and i've seen a server do this too
				ResetStream(meta.Stream.Id, SPDYResetReason.Cancel);
			}
		}

		/// <summary>Called when the connection has been closed.</summary>
		void OnConnectionClosed()
		{
			SetState(SPDYClientState.Closed);
			ClearControlQueue();
			lock(streams)
			{
				foreach(StreamMeta meta in streams.Values) meta.OnConnectionClosed();
			}
			conn.Dispose();
		}

		/// <summary>Enqueues a control frame in the control frame queue.</summary>
		/// <param name="frame">The <see cref="SPDYFrame"/> to enqueue</param>
		/// <param name="highPriority">If true, the frame will be added to the beginning of the queue. The default is false.</param>
		void EnqueueControlFrame(SPDYFrame frame, bool highPriority = false)
		{
			lock(controlQueue)
			{
				if(highPriority) controlQueue.AddFirst(frame);
				else controlQueue.AddLast(frame);
			}
			writeEvent.Set();
		}

		/// <summary>Sends a <see cref="SPDYFrameType.GoAway"/> frame and optionally begins shutting down.</summary>
		/// <param name="reason">The <see cref="SPDYShutdownReason"/> for the shutdown</param>
		/// <param name="closeAfterSending">If true, the connection will be closed after the next <see cref="SPDYFrameType.GoAway"/>
		/// frame is sent - not necessarily this one, so you may want to pair this with <paramref name="highPriority"/>.
		/// </param>
		/// <param name="highPriority">If true, the <see cref="SPDYFrameType.GoAway"/> frame will be added to the beginning of the queue.</param>
		void GoAway(SPDYShutdownReason reason, bool closeAfterSending, bool highPriority)
		{
			int lastAcceptedStream;
			lock(streams) lastAcceptedStream = this.lastAcceptedStream;
			var frame = SPDYFrame.GoAway(lastAcceptedStream, reason);
			lock(controlQueue)
			{
				fastShutDown |= closeAfterSending;
				SetState(closeAfterSending ? SPDYClientState.Closing : SPDYClientState.GoAwaySent);
				if(closeAfterSending & highPriority) ClearControlQueue();
				EnqueueControlFrame(frame, highPriority);
			}
		}

		/// <summary>Handles a <see cref="SPDYFrameType.GoAway"/> frame sent by the other side, telling us not to create any more streams.</summary>
		void HandleGoAway(SPDYFrame frame)
		{
			SetState(SPDYClientState.GoAwayReceived);
			int lastAcceptedStream = frame.ReadInt(0); // get the ID of the last stream accepted by the other side
			lock(streams) // close all streams whose IDs are greater than that, as no Reset frame will be forthcoming
			{
				var deadStreams = new List<int>();
				foreach(KeyValuePair<int, StreamMeta> pair in streams)
				{
					if(pair.Key > lastAcceptedStream && !IsRemoteId(pair.Key)) deadStreams.Add(pair.Key);
				}
				foreach(int deadStreamId in deadStreams) OnStreamReset(deadStreamId, SPDYResetReason.RefusedStream);
			}
		}

		/// <summary>Handles a request to create a new stream.</summary>
		void HandleNewStream(SPDYFrame frame)
		{
			int streamId = frame.ReadInt(0), associatedStreamId = frame.ReadInt(4), priority = frame.Data[8] >> 5;
			// we have to decompress the headers even if we're not going to use them in order to keep the decompressor in sync
			Dictionary<string, List<string>> headers = conn.DecompressHeaders(frame.Data, 10, frame.DataLength-10);
			if(streamId <= lastReceivedStream || associatedStreamId >= streamId || !IsRemoteId(streamId))
			{
				OnProtocolError();
			}
			else if(!WasGoAwaySent) // we shouldn't respond to new stream requests - not even to decline - after sending GOAWAY
			{
				if(frame.Version != 3) // we only support SPDY 3.x
				{
					ResetStream(streamId, SPDYResetReason.UnsupportedVersion);
				}
				else
				{
					lastReceivedStream = streamId;
					if(frame.HasFlags(SPDYFrameFlags.Final | SPDYFrameFlags.Unidirectional)) // reject unusable streams
					{
						EnqueueControlFrame(SPDYFrame.Reset(streamId, SPDYResetReason.RefusedStream));
					}
					else
					{
						var stream = new SPDYStream(this, streamId, associatedStreamId, !frame.HasFlags(SPDYFrameFlags.Final),
							!frame.HasFlags(SPDYFrameFlags.Unidirectional), true, headers);
						if(ShouldAccept != null && !ShouldAccept(stream)) // if we shouldn't accept this stream...
						{
							stream.SetState(SPDYStreamState.Closed); // prevent it from trying to finish itself, etc
							EnqueueControlFrame(SPDYFrame.Reset(streamId, SPDYResetReason.RefusedStream));
						}
						else // otherwise, welcome the new addition to our family
						{
							var meta = new StreamMeta(stream, priority, true, defaultInWindowSize, defaultOutWindowSize);
							lock(streams)
							{
								lastAcceptedStream = streamId; // set this in the lock because it's used by GoAway, which users can call
								streams.Add(streamId, meta);
								if(stream.CanWrite) AddStreamToWriteList(meta);
								EnqueueControlFrame(SPDYFrame.Reply(streamId, conn.CompressHeaders(null)));
							}
							StreamOpened?.Invoke(stream);
						}
					}
				}
			}
		}

		/// <summary>Handles the acceptance of a stream we created.</summary>
		void HandleStreamAccepted(SPDYFrame frame)
		{
			int streamId = frame.ReadInt(0);
			// we have to decompress the headers even if we're not going to use them in order to keep the decompressor in sync
			var headers = conn.DecompressHeaders(frame.Data, 4, frame.DataLength-4);
			StreamMeta meta;
			lock(streams) streams.TryGetValue(streamId, out meta);
			var resetReason = SPDYResetReason.NotReset;
			if(meta == null) resetReason = SPDYResetReason.InvalidStream;
			else if(IsRemoteId(streamId)) resetReason = SPDYResetReason.ProtocolError; // they're accepting a stream we didn't create
			else if(meta.Accepted && meta.Stream.WasFinishReceived) resetReason = SPDYResetReason.StreamAlreadyClosed;
			else if(meta.Accepted && meta.Stream.State != SPDYStreamState.HalfOpen) resetReason = SPDYResetReason.StreamInUse;
			if(resetReason != SPDYResetReason.NotReset)
			{
				ResetStream(streamId, resetReason);
			}
			else
			{
				meta.Stream.AddHeadersCore(headers);
				meta.Stream.SetState(frame.IsFinal ? SPDYStreamState.FinishReceived : SPDYStreamState.Open);
				meta.Accepted = true; // mark the stream as having been accepted
				if(meta.Stream.State != SPDYStreamState.Closed) StreamOpened?.Invoke(meta.Stream);
				CloseStreamIfComplete(meta); // if we were just waiting for acceptance before closing the stream, close it now
			}
		}

		/// <summary>Handles data received for a stream.</summary>
		void HandleStreamData(SPDYFrame frame)
		{
			var resetReason = SPDYResetReason.NotReset;
			lock(streams)
			{
				if(!streams.TryGetValue(frame.StreamId, out StreamMeta meta))
				{
					resetReason = SPDYResetReason.InvalidStream;
				}
				else if(meta.Stream.WasFinishReceived)
				{
					resetReason = SPDYResetReason.StreamAlreadyClosed;
				}
				else
				{
					if(frame.IsFinal) meta.Stream.SetState(SPDYStreamState.FinishReceived);
					meta.Stream.QueueData(frame.Data, frame.DataLength); // add the data to the stream and wake up waiting readers
					if(UseFlowControl)
					{
						meta.inWindowSpace -= frame.DataLength; // reduce the stream's window size
						inWindowSpace -= frame.DataLength; // and the global window size
						if((inWindowSpace | meta.inWindowSpace) < 0) resetReason = SPDYResetReason.FlowControlError;
					}
					if(frame.IsFinal && frame.DataLength == 0 && resetReason == SPDYResetReason.NotReset) CloseStreamIfComplete(meta);
				}
			}
			if(resetReason != SPDYResetReason.NotReset) ResetStream(frame.StreamId, resetReason);
		}

		/// <summary>Handles a stream being closed by the other side.</summary>
		void HandleStreamReset(SPDYFrame frame) => OnStreamReset(frame.ReadInt(0), (SPDYResetReason)frame.ReadInt(4));

		/// <summary>Handles the receipt of new connection settings.</summary>
		void HandleSettings(SPDYFrame frame)
		{
			for(int index = 4, count = frame.ReadInt(0), i = 0; i < count; index += 8, i++) // for each setting...
			{
				var setting = new SPDYSetting(frame.Data, index); // read the setting from the data array
				if(setting.ID == SPDYSettingId.InitialWindowSize) // if it's the default window size setting...
				{
					int delta = (int)setting.Value - defaultOutWindowSize;
					defaultOutWindowSize = (int)setting.Value;
					if(UseFlowControl) // if we're using flow control, update the windows of existing streams based on the difference.
					{                  // note that this may reduce outWindowSpace below zero, which is legal
						lock(streams)
						{
							foreach(StreamMeta meta in streams.Values) meta.outWindowSpace += delta;
						}
					}
					break; // the window size is the only setting we care about
				}
			}
		}

		/// <summary>Determines whether an ID for an object created by the other side is valid.</summary>
		bool IsRemoteId(int id)
		{
			id &= 1; // we only care about even or odd here
			return isClient ? id == 0 : id != 0; // clients use odd IDs and servers use even IDs, and the ID is from the remote side
		}

		/// <summary>Called when a protocol error is detected and begins a rapid but graceful shutdown of the connection.</summary>
		void OnProtocolError() => GoAway(SPDYShutdownReason.ProtocolError, true, true);

		/// <summary>Called when a stream has been reset, either by us or by the other side.</summary>
		void OnStreamReset(int streamId, SPDYResetReason reason)
		{
			StreamMeta meta;
			lock(streams)
			{
				if(streams.TryGetValue(streamId, out meta))
				{
					if(meta.Stream.CanWrite) writableStreamList.Remove(meta);
					streams.Remove(streamId);
					// if we're closing, removing a stream will move us towards being closed, so let the write task know
					if(State >= SPDYClientState.Closing) writeEvent.Set();
				}
			}
			if(meta != null)
			{
				meta.Stream.OnReset(reason);
				StreamReset?.Invoke(meta.Stream);
			}
		}

		/// <summary>Queues data for a stream and/or communicates the finalization of one.</summary>
		void QueueData(SPDYStream stream, ReadOnlySpan<byte> data, bool final)
		{
			StreamMeta meta;
			lock(streams) streams.TryGetValue(stream.Id, out meta);
			if(meta == null) throw new InvalidOperationException("The stream has been closed.");
			if(!meta.Finalized) // if the stream isn't already finalized...
			{
				meta.Enqueue(data, final); // enqueue the new data, if any
				if(final) meta.Finalized = true; // and set the finalized flag
				writeEvent.Set(); // let the write task know about the new data and/or flag
			}
		}

		/// <summary>Called to reset a stream.</summary>
		void ResetStream(int streamId, SPDYResetReason reason)
		{
			EnqueueControlFrame(SPDYFrame.Reset(streamId, reason));
			OnStreamReset(streamId, reason);
		}

		/// <summary>Attempts to update to the given state.</summary>
		void SetState(SPDYClientState newState)
		{
			lock(controlQueue)
			{
				SPDYClientState oldState = State;
				if(newState > State)
				{
					if(newState == SPDYClientState.GoAwaySent && State == SPDYClientState.GoAwayReceived) State = SPDYClientState.Closing;
					else State = newState;
				}
				else if(newState == SPDYClientState.GoAwayReceived && State == SPDYClientState.GoAwaySent)
				{
					State = SPDYClientState.Closing;
				}
				// if we transitioned to a closing state (or beyond), let the write task know so it can perform its closing logic
				if(newState >= SPDYClientState.Closing && oldState < SPDYClientState.Closing) writeEvent.Set();
			}
		}

		/// <summary>Handle a window space update from the other side.</summary>
		void UpdateWindowSpace(SPDYFrame frame)
		{
			int streamId = frame.ReadInt(0), delta = frame.ReadInt(4);
			if((streamId | delta) <= 0)
			{
				OnProtocolError();
			}
			else if(UseFlowControl)
			{
				int oldSpace = -1;
				lock(streams)
				{
					if(streamId == 0)
					{
						oldSpace = outWindowSpace;
						outWindowSpace += delta;
					}
					else if(streams.TryGetValue(streamId, out StreamMeta meta))
					{
						oldSpace = Math.Min(outWindowSpace, meta.outWindowSpace);
						meta.outWindowSpace += delta;
					}
					else
					{
						ResetStream(streamId, SPDYResetReason.InvalidStream);
					}
				}
				if(oldSpace == 0) writeEvent.Set(); // if this update gives us space where we previously had none, wake up the writer
			}
		}

		/// <summary>Handle additional stream headers sent by the other side.</summary>
		void UpdateStreamHeaders(SPDYFrame frame)
		{
			int streamId = frame.ReadInt(0);
			// we have to decompress the headers even if we're not going to use them in order to keep the decompressor in sync
			Dictionary<string, List<string>> headers = conn.DecompressHeaders(frame.Data, 4, frame.DataLength-4);
			StreamMeta meta;
			lock(streams)
			{
				if(streams.TryGetValue(streamId, out meta) && frame.IsFinal) meta.Stream.SetState(SPDYStreamState.FinishReceived);
			}
			var resetReason = SPDYResetReason.NotReset;
			if(meta == null) resetReason = SPDYResetReason.InvalidStream;
			else if(meta.Stream.WasFinishReceived) resetReason = SPDYResetReason.StreamAlreadyClosed;
			if(resetReason != SPDYResetReason.NotReset)
			{
				ResetStream(streamId, resetReason);
			}
			else
			{
				meta.Stream.AddHeadersCore(headers);
				StreamUpdated?.Invoke(meta.Stream);
			}
		}

		/// <summary>Sends all queued control frames.</summary>
		/// <returns>Returns -1 if the client should now shut down, 0 if no control frames were sent, and 1 if some frames were sent.</returns>
		async Task<int> WriteControlFrames(CancellationToken cancelToken)
		{
			bool sentAnything = false;
			while(true)
			{
				SPDYFrame frame;
				lock(controlQueue)
				{
					if(controlQueue.Count == 0)
					{
						if(fastShutDown) return -1;
						break;
					}
					frame = controlQueue.First.Value;
					controlQueue.RemoveFirst();
				}
				await conn.SendFrameAsync(frame, cancelToken).ConfigureAwait(false);
				frame.Dispose();
				if(fastShutDown && frame.Type == SPDYFrameType.GoAway) return -1;
				sentAnything = true;
			}
			return sentAnything ? 1 : 0;
		}

		/// <summary>A background task that handles writing all output frames.</summary>
		async Task WriteData(CancellationToken cancelToken)
		{
			byte[] buffer = ArrayPool<byte>.Shared.Rent(64*1024);
			while(!cancelToken.IsCancellationRequested && State != SPDYClientState.Closed)
			{
				try
				{
					LinkedListNode<StreamMeta> node;
					int cfResult; // the result of writing control frames (1 = frames written, 0 = nothing written, -1 = time to exit)
					int dataFramePriority = int.MaxValue; // the priority of the highest-priority node that had a frame to write
					bool sentAnything = false; // whether we sent any frames on this iteration of the outer loop
					lock(streams) node = writableStreamList.First; // start going through the streams
					// we want to give higher-priority streams precedence over lower-priority streams. we do this by going through streams
					// in priority order (since the stream list is sorted), and once we find a stream with data to write, we only service
					// other streams of equal priority. thus, we only service lower-priority streams when higher-priority ones are idle
					while(node != null && node.Value.Priority <= dataFramePriority)
					{
						SPDYFrame dataFrame = default(SPDYFrame); // the data frame to write, if any
						StreamMeta meta = null; // the stream whose data frame is being sent, if any
						lock(streams)
						{
							do // find a stream with a data frame to write
							{
								meta = node.Value;
								if(meta.OutputBuffer.DataAvailable != 0 || meta.Finalized && !meta.SentFinalFrame) // something to write?
								{
									int toRead = buffer.Length; // get the maximum amount of data we can send
									if(UseFlowControl) toRead = Math.Min(Math.Min(meta.outWindowSpace, outWindowSpace), toRead);
									if(toRead != 0 || meta.OutputBuffer.DataAvailable == 0) // if we have window space, or if we only
									{                                                       // need to send the final, empty frame...
										int read = meta.OutputBuffer.Read(buffer, 0, toRead); // read as much as we can, up to the maximum
										dataFrame = SPDYFrame.DataFrame(
											meta.Stream.Id, new ReadOnlySpan<byte>(buffer, 0, read), meta.Finalized);
										if(UseFlowControl) // if flow control is enabled, shrink the output windows
										{
											outWindowSpace -= read;
											meta.outWindowSpace -= read;
										}
										if(dataFrame.IsFinal) writableStreamList.Remove(node); // if it was the final frame, remove the stream
										dataFramePriority = meta.Priority; // remember the priority of the stream we're sending data for
									}
								}
								node = node.Next; // move to the next stream
							} while(node != null && node.Value.Priority <= dataFramePriority && !dataFrame.IsValid);
						}
						if(dataFrame.IsValid) // if we found a data frame to write...
						{
							cfResult = await WriteControlFrames(cancelToken).ConfigureAwait(false); // first write any control frames,
							if(cfResult < 0) { dataFrame.Dispose(); goto done; }                    // which are always top priority
							else if(cfResult > 0) sentAnything = true;

							await conn.SendFrameAsync(dataFrame, cancelToken).ConfigureAwait(false); // then write the data frame
							if(dataFrame.IsFinal) // if it was the final frame for the stream...
							{
								meta.SentFinalFrame = true; // remember that
								// if we created the stream and we're done with it and the other side accepted it already, close it by
								// sending a Reset frame. (if it hasn't been accepted yet, we'll close it as soon as it's accepted)
								if(meta.Stream.State == SPDYStreamState.Closing && !IsRemoteId(dataFrame.StreamId) && meta.Accepted)
								{
									await conn.SendFrameAsync(SPDYFrame.Reset(meta.Stream.Id, SPDYResetReason.Cancel)).ConfigureAwait(false);
									OnStreamReset(meta.Stream.Id, SPDYResetReason.Cancel);
								}
							}
							dataFrame.Dispose();
							sentAnything = true;
						}
					}

					// after sending the last data frame, or in case we didn't send any data frames at all, look for control frames again
					cfResult = await WriteControlFrames(cancelToken).ConfigureAwait(false);
					if(cfResult < 0) break;
					else if(cfResult > 0) sentAnything = true;

					if(!sentAnything) // if there was no data to send...
					{
						// if we're supposed to be exiting and all streams are closed, then we should exit
						if(State == SPDYClientState.Closing && streams.Count == 0) break;
						await writeEvent.WaitAsync(cancelToken).ConfigureAwait(false); // otherwise wait for something interesting
					}
				}
				catch(InvalidOperationException) { break; } // if the connection was closed, we should exit
				catch(OperationCanceledException) { } // ditto for a cancellation, but we'll let the loop check that
			}
			done:
			SetState(SPDYClientState.Closed);
			conn.Dispose();
			ArrayPool<byte>.Shared.Return(buffer);
		}

		readonly SPDYConnection conn;
		readonly LinkedList<SPDYFrame> controlQueue = new LinkedList<SPDYFrame>();
		readonly Dictionary<int,StreamMeta> streams = new Dictionary<int,StreamMeta>();
		readonly LinkedList<StreamMeta> writableStreamList = new LinkedList<StreamMeta>();
		readonly AsyncAutoResetEvent writeEvent = new AsyncAutoResetEvent();
		readonly bool isClient;
		CancellationTokenSource writeCts;
		int defaultInWindowSize = 64*1024, inWindowPending, inWindowSize, inWindowSpace = 64*1024;
		int defaultOutWindowSize = 64*1024, outWindowSpace = 64*1024;
		int lastAcceptedStream, lastReceivedStream, nextStreamId = 1;
		bool _useFlowControl = true, fastShutDown;
	}

	/// <summary>Describes the current state of a <see cref="SPDYClient"/>.</summary>
	public enum SPDYClientState
	{
		/// <summary>The client has not been started because <see cref="SPDYClient.RunAsync"/> has not been called yet.</summary>
		NotStarted = 0,
		/// <summary>The client is currently running.</summary>
		Open = 1,
		/// <summary>The client is currently running but the remote side asked us to go away (i.e. not create any more streams and finish
		/// processing existing streams).
		/// </summary>
		GoAwayReceived = 2,
		/// <summary>The client is currently running but we asked the remote side to go away (i.e. not create any more streams and finish
		/// processing existing streams).
		/// </summary>
		GoAwaySent = 3,
		/// <summary>The client is shutting down gracefully (possibly because both sides to each other to go away).</summary>
		Closing = 4,
		/// <summary>The client is closed.</summary>
		Closed = 5
	}
}

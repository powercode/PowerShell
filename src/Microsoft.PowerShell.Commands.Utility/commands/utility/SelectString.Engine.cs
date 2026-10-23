// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// Sequential search mechanisms shared by the cmdlet and standalone benchmarks.
    /// This file must not depend on PowerShell assemblies or pipeline services.
    /// </summary>
    internal sealed class SelectStringEngine
    {
        private readonly string[] _patterns;
        private readonly Regex[] _regexes;
        private readonly bool _allMatches;
        private readonly bool _notMatch;
        private readonly CompareInfo _compareInfo;
        private readonly CompareOptions _compareOptions;

        internal SelectStringEngine(string[] patterns, bool simpleMatch = false, bool caseSensitive = false,
            bool allMatches = false, bool notMatch = false, CompareInfo compareInfo = null,
            CompareOptions? compareOptions = null)
        {
            _patterns = (string[])patterns.Clone();
            _allMatches = allMatches;
            _notMatch = notMatch;
            _compareInfo = compareInfo ?? CultureInfo.CurrentCulture.CompareInfo;
            _compareOptions = compareOptions ?? (caseSensitive ? CompareOptions.None : CompareOptions.IgnoreCase);
            if (!simpleMatch)
            {
                _regexes = new Regex[patterns.Length];
                for (int i = 0; i < patterns.Length; i++)
                {
                    try
                    {
                        _regexes[i] = new Regex(patterns[i], caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                    }
                    catch (Exception e)
                    {
                        throw new InvalidPatternException(i, e);
                    }
                }
            }
        }

        internal sealed class InvalidPatternException : Exception
        {
            internal InvalidPatternException(int patternIndex, Exception cause)
                : base(cause.Message, cause)
            {
                PatternIndex = patternIndex;
            }

            internal int PatternIndex { get; }
        }

        internal sealed class ResultContext
        {
            internal string[] PreContext { get; set; }

            internal string[] PostContext { get; set; }

            internal string[] DisplayPreContext { get; set; }

            internal string[] DisplayPostContext { get; set; }
        }

        // Strings and regex matches own their storage. No result borrows reader buffers.
        internal sealed class Result
        {
            internal string Line { get; set; }

            internal ulong LineNumber { get; set; }

            internal string Path { get; set; }

            internal int PatternIndex { get; set; }

            internal int SimpleMatchIndex { get; set; } = -1;

            internal Match[] Matches { get; set; } = Array.Empty<Match>();

            internal ResultContext Context { get; set; }
        }

        /// <summary>
        /// Matching metadata only. Text ownership and output representation belong to the caller.
        /// </summary>
        internal readonly struct MatchData(int patternIndex, int simpleMatchIndex, Match[] matches)
        {
            internal int PatternIndex { get; } = patternIndex;

            internal int SimpleMatchIndex { get; } = simpleMatchIndex;

            internal Match[] Matches { get; } = matches;
        }

        /// <summary>
        /// Constructs and accesses a caller-owned result. Context trackers share the same
        /// reference, so logical and display context updates reach the final output object.
        /// Implementations must not retain borrowed buffers or reuse emitted results.
        /// </summary>
        internal interface IResultFactory<T> where T : class
        {
            T Create(string line, string path, ulong lineNumber, in MatchData match, bool context);

            string GetLine(T result);

            bool HasContext(T result);

            void SetLogicalContext(T result, string[] before, string[] after);

            void SetDisplayContext(T result, string[] before, string[] after);
        }

        // Default representation for standalone callers and lightweight output modes.
        internal readonly struct ResultFactory : IResultFactory<Result>
        {
            public Result Create(string line, string path, ulong lineNumber, in MatchData match, bool context) => new()
            {
                Line = line,
                Path = path,
                LineNumber = lineNumber,
                PatternIndex = match.PatternIndex,
                SimpleMatchIndex = match.SimpleMatchIndex,
                Matches = match.Matches,
                Context = context ? new ResultContext() : null,
            };

            public string GetLine(Result result) => result.Line;

            public bool HasContext(Result result) => result.Context != null;

            public void SetLogicalContext(Result result, string[] before, string[] after)
            {
                result.Context.PreContext = before;
                result.Context.PostContext = after;
            }

            public void SetDisplayContext(Result result, string[] before, string[] after)
            {
                result.Context.DisplayPreContext = before;
                result.Context.DisplayPostContext = after;
            }
        }

        internal bool TryMatch(string line, out MatchData result)
        {
            result = default;
            bool matched = false;
            int patternIndex = 0;
            int simpleIndex = -1;
            Match[] matches = null;
            for (; patternIndex < _patterns.Length; patternIndex++)
            {
                if (_regexes == null)
                {
                    simpleIndex = _compareInfo.IndexOf(line, _patterns[patternIndex], 0, line.Length, _compareOptions);
                    matched = simpleIndex >= 0;
                }
                else if (_allMatches && !_notMatch)
                {
                    MatchCollection collection = _regexes[patternIndex].Matches(line);
                    matched = collection.Count > 0;
                    if (matched)
                    {
                        matches = new Match[collection.Count];
                        ((ICollection)collection).CopyTo(matches, 0);
                    }
                }
                else
                {
                    Match match = _regexes[patternIndex].Match(line);
                    matched = match.Success;
                    if (matched)
                    {
                        matches = new[] { match };
                    }
                }

                if (matched)
                {
                    break;
                }
            }

            if (_notMatch)
            {
                matched = !matched;
                patternIndex = 0;
            }

            if (!matched)
            {
                return false;
            }

            result = new MatchData(patternIndex, simpleIndex, matches ?? Array.Empty<Match>());
            return true;
        }

        internal static IContextTracker<T> CreateContextTracker<T, TFactory>(int preContext, int postContext, TFactory factory)
            where T : class
            where TFactory : struct, IResultFactory<T> =>
            preContext == 0 && postContext == 0 ? new NoContextTracker<T>() : new ContextTracker<T, TFactory>(preContext, postContext, factory);

        internal bool ScanFile(string path, Encoding encoding, int preContext, int postContext,
            bool stopAfterFirst, Func<Result, bool> emit, Func<bool> shouldStop = null,
            int readerBufferSize = 0, CancellationToken cancellationToken = default) =>
            ScanFile<Result, ResultFactory>(path, encoding, preContext, postContext, stopAfterFirst,
                default, emit, shouldStop, readerBufferSize, cancellationToken);

        /// <summary>
        /// Scans decoded file lines in order. A false sink result stops reading.
        /// With stopAfterFirst the file is closed before the completed result is emitted.
        /// The sink runs synchronously on the caller's thread; no whole-file buffering occurs.
        /// </summary>
        internal bool ScanFile<T, TFactory>(string path, Encoding encoding, int preContext, int postContext,
            bool stopAfterFirst, TFactory factory, Func<T, bool> emit, Func<bool> shouldStop = null,
            int readerBufferSize = 0, CancellationToken cancellationToken = default)
            where T : class
            where TFactory : struct, IResultFactory<T>
        {
            IContextTracker<T> tracker = CreateContextTracker<T, TFactory>(preContext, postContext, factory);
            bool found = false;
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: readerBufferSize == 0 ? SelectReaderBufferSize(stream, encoding, stopAfterFirst) : readerBufferSize))
            {
                ulong lineNumber = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (shouldStop?.Invoke() == true)
                    {
                        return found;
                    }

                    string line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    lineNumber++;
                    if (TryMatch(line, out MatchData match))
                    {
                        T result = factory.Create(line, path, lineNumber, in match, preContext > 0 || postContext > 0);
                        tracker.TrackMatch(result);
                    }
                    else
                    {
                        tracker.TrackLine(line);
                    }

                    if (tracker.EmitQueue.Count > 0)
                    {
                        found = true;
                        if (stopAfterFirst)
                        {
                            break;
                        }

                        foreach (T ready in tracker.EmitQueue)
                        {
                            if (!emit(ready))
                            {
                                return true;
                            }
                        }

                        tracker.EmitQueue.Clear();
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (shouldStop?.Invoke() == true)
            {
                return found;
            }

            tracker.TrackEOF();
            foreach (T ready in tracker.EmitQueue)
            {
                found = true;
                if (!emit(ready) || stopAfterFirst)
                {
                    break;
                }
            }

            return found;
        }

        // StreamReader decodes a buffer of bytes before returning a line. Increasing that
        // buffer can therefore change behavior, not just throughput. For example, a file
        // may start with "hit\n" and contain an invalid UTF-8 byte at offset 8192. With
        // strict decoding, a 1 KiB reader can emit the first match and stop; a 16 KiB
        // reader throws while decoding its first buffer, before returning that match.
        // A downstream consumer can stop the scan too, so protecting Quiet/List alone
        // would not preserve this behavior for strict decoders.
        //
        // Retain the original 1 KiB buffer for early-exit modes and for strict or custom
        // decoding. Only runtime-provided UTF-8 encodings with replacement fallback opt
        // into larger reads: malformed bytes are replaced rather than throwing, and no
        // user-defined decoder/fallback code observes a different read-ahead boundary.
        // BOM detection remains enabled and can still select a different encoding.
        //
        // Larger buffers allocate more memory per opened file, even for tiny files or
        // an immediate match. Measurements favored 16 KiB for full scans of large files;
        // the 1 MiB size gate is conservative, not a universal break-even point. See
        // test/perf/SelectString/RESULTS-buffer-policy.md for the measured tradeoffs.
        // A nonzero ScanFile readerBufferSize bypasses selection for controlled benchmarks.
        private static int SelectReaderBufferSize(FileStream stream, Encoding encoding, bool stopAfterFirst)
        {
            if (stopAfterFirst || !stream.CanSeek || stream.Length < 1024 * 1024)
            {
                return 1024;
            }

            return encoding is UTF8Encoding &&
                encoding.GetType().Assembly == typeof(UTF8Encoding).Assembly &&
                encoding.DecoderFallback is DecoderReplacementFallback ? 16384 : 1024;
        }

        /// <summary>
        /// A generic circular buffer.
        /// </summary>
        /// <typeparam name="T">The type of items that are buffered.</typeparam>
        private sealed class CircularBuffer<T> : ICollection<T>
        {
            // Ring of items
            private readonly T[] _items;

            // Current length, as opposed to the total capacity
            // Current start of the list. Starts at 0, but may
            // move forwards or wrap around back to 0 due to
            // rotation.
            private int _firstIndex;

            /// <summary>
            /// Initializes a new instance of the <see cref="CircularBuffer{T}"/> class.
            /// </summary>
            /// <param name="capacity">The maximum capacity of the buffer.</param>
            /// <exception cref="ArgumentOutOfRangeException">If <paramref name="capacity"/> is negative.</exception>
            public CircularBuffer(int capacity)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(capacity);

                _items = new T[capacity];
                Clear();
            }

            /// <summary>
            /// Gets the maximum capacity of the buffer. If more items
            /// are added than the buffer has capacity for, then
            /// older items will be removed from the buffer with
            /// a first-in, first-out policy.
            /// </summary>
            public int Capacity => _items.Length;

            /// <summary>
            /// Whether or not the buffer is at capacity.
            /// </summary>
            public bool IsFull => Count == Capacity;

            /// <summary>
            /// Convert from a 0-based index to a buffer index which
            /// has been properly offset and wrapped.
            /// </summary>
            /// <param name="zeroBasedIndex">The index to wrap.</param>
            /// <exception cref="ArgumentOutOfRangeException">If <paramref name="zeroBasedIndex"/> is out of range.</exception>
            /// <returns>
            /// The actual index that <paramref name="zeroBasedIndex"/>
            /// maps to.
            /// </returns>
            private int WrapIndex(int zeroBasedIndex)
            {
                if (Capacity == 0 || zeroBasedIndex < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
                }

                return (zeroBasedIndex + _firstIndex) % Capacity;
            }

            #region IEnumerable<T> implementation.
            public IEnumerator<T> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return _items[WrapIndex(i)];
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return (IEnumerator)GetEnumerator();
            }
            #endregion

            #region ICollection<T> implementation
            public int Count { get; private set; }

            public bool IsReadOnly => false;

            /// <summary>
            /// Adds an item to the buffer. If the buffer is already
            /// full, the oldest item in the list will be removed,
            /// and the new item added at the logical end of the list.
            /// </summary>
            /// <param name="item">The item to add.</param>
            public void Add(T item)
            {
                if (Capacity == 0)
                {
                    return;
                }

                int itemIndex;

                if (IsFull)
                {
                    itemIndex = _firstIndex;
                    _firstIndex = (_firstIndex + 1) % Capacity;
                }
                else
                {
                    itemIndex = _firstIndex + Count;
                    Count++;
                }

                _items[itemIndex] = item;
            }

            public void Clear()
            {
                _firstIndex = 0;
                Count = 0;
            }

            public bool Contains(T item)
            {
                throw new NotImplementedException();
            }

            public void CopyTo(T[] array, int arrayIndex)
            {
                ArgumentNullException.ThrowIfNull(array);
                ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);

                if (Count > (array.Length - arrayIndex))
                {
                    throw new ArgumentException("arrayIndex");
                }

                // Iterate through the buffer in correct order.
                foreach (T item in this)
                {
                    array[arrayIndex++] = item;
                }
            }

            public bool Remove(T item)
            {
                throw new NotImplementedException();
            }
            #endregion

            /// <summary>
            /// Create an array of the items in the buffer. Items
            /// will be in the same order they were added.
            /// </summary>
            /// <returns>The new array.</returns>
            public T[] ToArray()
            {
                T[] result = new T[Count];
                CopyTo(result, 0);
                return result;
            }

            /// <summary>
            /// Access an item in the buffer. Indexing is based off
            /// of the order items were added, rather than any
            /// internal ordering the buffer may be maintaining.
            /// </summary>
            /// <param name="index">The index of the item to access.</param>
            /// <returns>The buffered item at index <paramref name="index"/>.</returns>
            public T this[int index]
            {
                get
                {
                    if (!(index >= 0 && index < Count))
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return _items[WrapIndex(index)];
                }
            }
        }

        /// <summary>
        /// An interface to a context tracking algorithm.
        /// </summary>
        internal interface IContextTracker<T> where T : class
        {
            /// <summary>
            /// Gets matches with completed context information
            /// that are ready to be emitted into the pipeline.
            /// </summary>
            // Keep the concrete type so draining a queue uses List<T>'s struct
            // enumerator without boxing it through IEnumerable<T>.
            List<T> EmitQueue { get; }

            /// <summary>
            /// Track a non-matching line for context.
            /// </summary>
            /// <param name="line">The line to track.</param>
            void TrackLine(string line);

            /// <summary>
            /// Track a matching line.
            /// </summary>
            /// <param name="match">The line to track.</param>
            void TrackMatch(T match);

            /// <summary>
            /// Track having reached the end of the file,
            /// giving the tracker a chance to process matches with
            /// incomplete context information.
            /// </summary>
            void TrackEOF();
        }

        /// <summary>
        /// A state machine to track display context for each match.
        /// </summary>
        private sealed class DisplayContextTracker<T, TFactory> : IContextTracker<T>
            where T : class
            where TFactory : struct, IResultFactory<T>
        {
            private readonly TFactory _factory;

            private enum ContextState
            {
                InitialState,
                CollectPre,
                CollectPost,
            }

            private ContextState _contextState = ContextState.InitialState;
            private readonly int _preContext;
            private readonly int _postContext;

            // The context leading up to the match.
            private readonly CircularBuffer<string> _collectedPreContext;

            // The context after the match.
            private readonly List<string> _collectedPostContext;

            // Current match info we are tracking postcontext for.
            // At any given time, if set, this value will not be
            // in the emitQueue but will be the next to be added.
            private T _matchInfo = null;

            /// <summary>
            /// Initializes a new instance of the <see cref="DisplayContextTracker{T, TFactory}"/> class.
            /// </summary>
            /// <param name="preContext">How much preContext to collect at most.</param>
            /// <param name="postContext">How much postContext to collect at most.</param>
            /// <param name="factory">Access to the caller-owned result and its context.</param>
            public DisplayContextTracker(int preContext, int postContext, TFactory factory)
            {
                _factory = factory;
                _preContext = preContext;
                _postContext = postContext;

                _collectedPreContext = new CircularBuffer<string>(preContext);
                _collectedPostContext = new List<string>(postContext);
                _emitQueue = new List<T>();
                Reset();
            }

            #region IContextTracker<T> implementation
            public List<T> EmitQueue => _emitQueue;

            private readonly List<T> _emitQueue;

            // Track non-matching line
            public void TrackLine(string line)
            {
                switch (_contextState)
                {
                    case ContextState.InitialState:
                        break;
                    case ContextState.CollectPre:
                        _collectedPreContext.Add(line);
                        break;
                    case ContextState.CollectPost:
                        // We're not done collecting post-context.
                        _collectedPostContext.Add(line);

                        if (_collectedPostContext.Count >= _postContext)
                        {
                            // Now we're done.
                            UpdateQueue();
                        }

                        break;
                }
            }

            // Track matching line
            public void TrackMatch(T match)
            {
                // Update the queue in case we were in the middle
                // of collecting postcontext for an older match...
                if (_contextState == ContextState.CollectPost)
                {
                    UpdateQueue();
                }

                // Update the current matchInfo.
                _matchInfo = match;

                // If postContext is set, then we need to hold
                // onto the match for a while and gather context.
                // Otherwise, immediately move the match onto the queue
                // and let UpdateQueue update our state instead.
                if (_postContext > 0)
                {
                    _contextState = ContextState.CollectPost;
                }
                else
                {
                    UpdateQueue();
                }
            }

            // Track having reached the end of the file.
            public void TrackEOF()
            {
                // If we're in the middle of collecting postcontext, we
                // already have a match and it's okay to queue it up
                // early since there are no more lines to track context
                // for.
                if (_contextState == ContextState.CollectPost)
                {
                    UpdateQueue();
                }
            }
            #endregion

            /// <summary>
            /// Moves matchInfo, if set, to the emitQueue and
            /// resets the tracking state.
            /// </summary>
            private void UpdateQueue()
            {
                if (_matchInfo != null)
                {
                    _emitQueue.Add(_matchInfo);

                    if (_factory.HasContext(_matchInfo))
                    {
                        _factory.SetDisplayContext(_matchInfo, _collectedPreContext.ToArray(), _collectedPostContext.ToArray());
                    }

                    Reset();
                }
            }

            // Reset tracking state. Does not reset the emit queue.
            private void Reset()
            {
                _contextState = (_preContext > 0)
                               ? ContextState.CollectPre
                               : ContextState.InitialState;
                _collectedPreContext.Clear();
                _collectedPostContext.Clear();
                _matchInfo = null;
            }
        }

        /// <summary>
        /// A class to track logical context for each match.
        /// </summary>
        /// <remarks>
        /// The difference between logical and display context is
        /// that logical context includes as many context lines
        /// as possible for a given match, up to the specified
        /// limit, including context lines which overlap between
        /// matches and other matching lines themselves. Display
        /// context, on the other hand, is designed to display
        /// a possibly-continuous set of matches by excluding
        /// overlapping context (lines will only appear once)
        /// and other matching lines (since they will appear
        /// as their own match entries.).
        /// </remarks>
        private sealed class LogicalContextTracker<T, TFactory> : IContextTracker<T>
            where T : class
            where TFactory : struct, IResultFactory<T>
        {
            private readonly TFactory _factory;

            // A union: string | T. Needed since
            // context lines could be either proper matches
            // or non-matching lines.
            private readonly struct ContextEntry
            {
                public readonly string Line;
                public readonly T Match;

                public ContextEntry(string line)
                {
                    Line = line;
                }

                public ContextEntry(T match)
                {
                    Match = match;
                }

                public string GetLine(TFactory factory) => Match != null ? factory.GetLine(Match) : Line;
            }

            // Whether or not early entries found
            // while still filling up the context buffer
            // have been added to the emit queue.
            // Used by UpdateQueue.
            private bool _hasProcessedPreEntries;

            private readonly int _preContext;
            private readonly int _postContext;

            // A circular buffer tracking both precontext and postcontext.
            //
            // Essentially, the buffer is separated into regions:
            // | prectxt region  (older entries, length = precontext)  |
            // | match region    (length = 1)                          |
            // | postctxt region (newer entries, length = postcontext) |
            //
            // When context entries containing a match reach the "middle"
            // (the position between the pre/post context regions)
            // of this buffer, and the buffer is full, we will know
            // enough context to populate the Context properties of the
            // match. At that point, we will add the match object
            // to the emit queue.
            private readonly CircularBuffer<ContextEntry> _collectedContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="LogicalContextTracker{T, TFactory}"/> class.
            /// </summary>
            /// <param name="preContext">How much preContext to collect at most.</param>
            /// <param name="postContext">How much postContext to collect at most.</param>
            /// <param name="factory">Access to the caller-owned result and its context.</param>
            public LogicalContextTracker(int preContext, int postContext, TFactory factory)
            {
                _factory = factory;
                _preContext = preContext;
                _postContext = postContext;
                _collectedContext = new CircularBuffer<ContextEntry>(preContext + postContext + 1);
                _emitQueue = new List<T>();
            }

            #region IContextTracker<T> implementation
            public List<T> EmitQueue => _emitQueue;

            private readonly List<T> _emitQueue;

            public void TrackLine(string line)
            {
                ContextEntry entry = new(line);
                _collectedContext.Add(entry);
                UpdateQueue();
            }

            public void TrackMatch(T match)
            {
                ContextEntry entry = new(match);
                _collectedContext.Add(entry);
                UpdateQueue();
            }

            public void TrackEOF()
            {
                // If the buffer is already full,
                // check for any matches with incomplete
                // postcontext and add them to the emit queue.
                // These matches can be identified by being past
                // the "middle" of the context buffer (still in
                // the postcontext region.
                //
                // If the buffer isn't full, then nothing will have
                // ever been emitted and everything is still waiting
                // on postcontext. So process the whole buffer.
                int startIndex = _collectedContext.IsFull ? _preContext + 1 : 0;
                EmitAllInRange(startIndex, _collectedContext.Count - 1);
            }
            #endregion

            /// <summary>
            /// Add all matches found in the specified range
            /// to the emit queue, collecting as much context
            /// as possible up to the limits specified in the constructor.
            /// </summary>
            /// <remarks>
            /// The range is inclusive; the entries at
            /// startIndex and endIndex will both be checked.
            /// </remarks>
            /// <param name="startIndex">The beginning of the match range.</param>
            /// <param name="endIndex">The ending of the match range.</param>
            private void EmitAllInRange(int startIndex, int endIndex)
            {
                for (int i = startIndex; i <= endIndex; i++)
                {
                    T match = _collectedContext[i].Match;
                    if (match != null)
                    {
                        int preStart = Math.Max(i - _preContext, 0);
                        int postLength = Math.Min(_postContext, _collectedContext.Count - i - 1);
                        Emit(match, preStart, i - preStart, i + 1, postLength);
                    }
                }
            }

            /// <summary>
            /// Add match(es) found in the match region to the
            /// emit queue. Should be called every time an entry
            /// is added to the context buffer.
            /// </summary>
            private void UpdateQueue()
            {
                // Are we at capacity and thus have enough postcontext?
                // Is there a match in the "middle" of the buffer
                // that we know the pre/post context for?
                //
                // If this is the first time we've reached full capacity,
                // hasProcessedPreEntries will not be set, and we
                // should go through the entire context, because it might
                // have entries that never collected enough
                // precontext. Otherwise, we should just look at the
                // middle region.
                if (_collectedContext.IsFull)
                {
                    if (_hasProcessedPreEntries)
                    {
                        // Only process a potential match with exactly
                        // enough pre and post-context.
                        EmitAllInRange(_preContext, _preContext);
                    }
                    else
                    {
                        // Some of our early entries may not
                        // have enough precontext. Process them too.
                        EmitAllInRange(0, _preContext);
                        _hasProcessedPreEntries = true;
                    }
                }
            }

            /// <summary>
            /// Collects context from the specified ranges. Populates
            /// the specified match with the collected context
            /// and adds it to the emit queue.
            /// </summary>
            /// <remarks>
            /// Context ranges must be within the bounds of the context buffer.
            /// </remarks>
            /// <param name="match">The match to operate on.</param>
            /// <param name="preStartIndex">The start index of the preContext range.</param>
            /// <param name="preLength">The length of the preContext range.</param>
            /// <param name="postStartIndex">The start index of the postContext range.</param>
            /// <param name="postLength">The length of the postContext range.</param>
            private void Emit(T match, int preStartIndex, int preLength, int postStartIndex, int postLength)
            {
                if (_factory.HasContext(match))
                {
                    _factory.SetLogicalContext(match, CopyContext(preStartIndex, preLength), CopyContext(postStartIndex, postLength));
                }

                _emitQueue.Add(match);
            }

            /// <summary>
            /// Collects context from the specified ranges.
            /// </summary>
            /// <remarks>
            /// The range must be within the bounds of the context buffer.
            /// </remarks>
            /// <param name="startIndex">The index to start at.</param>
            /// <param name="length">The length of the range.</param>
            /// <returns>String representation of the collected context at the specified range.</returns>
            private string[] CopyContext(int startIndex, int length)
            {
                string[] result = new string[length];

                for (int i = 0; i < length; i++)
                {
                    result[i] = _collectedContext[startIndex + i].GetLine(_factory);
                }

                return result;
            }
        }

        /// <summary>
        /// A class to track both logical and display contexts.
        /// </summary>
        internal sealed class ContextTracker<T, TFactory> : IContextTracker<T>
            where T : class
            where TFactory : struct, IResultFactory<T>
        {
            private readonly DisplayContextTracker<T, TFactory> _displayTracker;
            private readonly LogicalContextTracker<T, TFactory> _logicalTracker;

            /// <summary>
            /// Initializes a new instance of the <see cref="ContextTracker{T, TFactory}"/> class.
            /// </summary>
            /// <param name="preContext">How much preContext to collect at most.</param>
            /// <param name="postContext">How much postContext to collect at most.</param>
            /// <param name="factory">Access to the caller-owned result and its context.</param>
            public ContextTracker(int preContext, int postContext, TFactory factory)
            {
                _displayTracker = new DisplayContextTracker<T, TFactory>(preContext, postContext, factory);
                _logicalTracker = new LogicalContextTracker<T, TFactory>(preContext, postContext, factory);
                EmitQueue = new List<T>();
            }

            #region IContextTracker<T> implementation
            public List<T> EmitQueue { get; }

            public void TrackLine(string line)
            {
                _displayTracker.TrackLine(line);
                _logicalTracker.TrackLine(line);
                UpdateQueue();
            }

            public void TrackMatch(T match)
            {
                _displayTracker.TrackMatch(match);
                _logicalTracker.TrackMatch(match);
                UpdateQueue();
            }

            public void TrackEOF()
            {
                _displayTracker.TrackEOF();
                _logicalTracker.TrackEOF();
                UpdateQueue();
            }
            #endregion

            /// <summary>
            /// Update the emit queue based on the wrapped trackers.
            /// </summary>
            private void UpdateQueue()
            {
                // Look for completed matches in the logical
                // tracker's queue. Since the logical tracker
                // will try to collect as much context as
                // possible, the display tracker will have either
                // already finished collecting its context for the
                // match or will have completed it at the same
                // time as the logical tracker, so we can
                // be sure the matches will have both logical
                // and display context already populated.
                foreach (T match in _logicalTracker.EmitQueue)
                {
                    EmitQueue.Add(match);
                }

                _logicalTracker.EmitQueue.Clear();
                _displayTracker.EmitQueue.Clear();
            }
        }

        /// <summary>
        /// ContextTracker that does not work for the case when pre- and post context is 0.
        /// </summary>
        internal sealed class NoContextTracker<T> : IContextTracker<T> where T : class
        {
            private readonly List<T> _matches = new List<T>(1);

            List<T> IContextTracker<T>.EmitQueue => _matches;

            void IContextTracker<T>.TrackLine(string line)
            {
            }

            void IContextTracker<T>.TrackMatch(T match) => _matches.Add(match);

            void IContextTracker<T>.TrackEOF()
            {
            }
        }

    }
}

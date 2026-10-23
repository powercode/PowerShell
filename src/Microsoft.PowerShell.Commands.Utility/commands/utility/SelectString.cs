// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.ExceptionServices;
using SearchResult = Microsoft.PowerShell.Commands.SelectStringEngine.Result;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// Context information about a match.
    /// </summary>
    public sealed class MatchInfoContext : ICloneable
    {
        internal MatchInfoContext()
        {
        }

        /// <summary>
        /// Gets or sets the lines found before a match.
        /// </summary>
        public string[] PreContext { get; set; }

        /// <summary>
        /// Gets or sets the lines found after a match.
        /// </summary>
        public string[] PostContext { get; set; }

        /// <summary>
        /// Gets or sets the lines found before a match. Does not include
        /// overlapping context and thus can be used to
        /// display contiguous match regions.
        /// </summary>
        public string[] DisplayPreContext { get; set; }

        /// <summary>
        /// Gets or sets the lines found after a match. Does not include
        /// overlapping context and thus can be used to
        /// display contiguous match regions.
        /// </summary>
        public string[] DisplayPostContext { get; set; }

        /// <summary>
        /// Produce a deep copy of this object.
        /// </summary>
        /// <returns>A new object that is a copy of this instance.</returns>
        public object Clone()
        {
            return new MatchInfoContext()
            {
                PreContext = (string[])PreContext?.Clone(),
                PostContext = (string[])PostContext?.Clone(),
                DisplayPreContext = (string[])DisplayPreContext?.Clone(),
                DisplayPostContext = (string[])DisplayPostContext?.Clone()
            };
        }
    }

    /// <summary>
    /// The object returned by select-string representing the result of a match.
    /// </summary>
    public class MatchInfo
    {
        private static readonly string s_inputStream = "InputStream";

        /// <summary>
        /// Gets or sets a value indicating whether the match was done ignoring case.
        /// </summary>
        /// <value>True if case was ignored.</value>
        public bool IgnoreCase { get; set; }

        /// <summary>
        /// Gets or sets the number of the matching line.
        /// </summary>
        /// <value>The number of the matching line.</value>
        public ulong LineNumber { get; set; }

        /// <summary>
        /// Gets or sets the text of the matching line.
        /// </summary>
        /// <value>The text of the matching line.</value>
        public string Line { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the matched portion of the string is highlighted.
        /// </summary>
        /// <value>Whether the matched portion of the string is highlighted with the negative VT sequence.</value>
        private readonly bool _emphasize;

        /// <summary>
        /// Stores the starting index of each match within the line.
        /// </summary>
        private readonly IReadOnlyList<int> _matchIndexes;

        /// <summary>
        /// Stores the length of each match within the line.
        /// </summary>
        private readonly IReadOnlyList<int> _matchLengths;

        /// <summary>
        /// Initializes a new instance of the <see cref="MatchInfo"/> class with emphasis disabled.
        /// </summary>
        public MatchInfo()
        {
            this._emphasize = false;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatchInfo"/> class with emphasized matched text.
        /// Used when virtual terminal sequences are supported.
        /// </summary>
        /// <param name="matchIndexes">Sets the matchIndexes.</param>
        /// <param name="matchLengths">Sets the matchLengths.</param>
        public MatchInfo(IReadOnlyList<int> matchIndexes, IReadOnlyList<int> matchLengths)
        {
            this._emphasize = true;
            this._matchIndexes = matchIndexes;
            this._matchLengths = matchLengths;
        }

        /// <summary>
        /// Gets the base name of the file containing the matching line.
        /// </summary>
        /// <remarks>
        /// It will be the string "InputStream" if the object came from the input stream.
        /// This is a readonly property calculated from the path <see cref="Path"/>.
        /// </remarks>
        /// <value>The file name.</value>
        public string Filename
        {
            get
            {
                if (!_pathSet)
                {
                    return s_inputStream;
                }

                return _filename ??= System.IO.Path.GetFileName(_path);
            }
        }

        private string _filename;

        /// <summary>
        /// Gets or sets the full path of the file containing the matching line.
        /// </summary>
        /// <remarks>
        /// It will be "InputStream" if the object came from the input stream.
        /// </remarks>
        /// <value>The path name.</value>
        public string Path
        {
            get => _pathSet ? _path : s_inputStream;
            set
            {
                _path = value;
                _pathSet = true;
            }
        }

        private string _path = s_inputStream;

        private bool _pathSet;

        /// <summary>
        /// Gets or sets the pattern that was used in the match.
        /// </summary>
        /// <value>The pattern string.</value>
        public string Pattern { get; set; }

        /// <summary>
        /// Gets or sets context for the match, or null if -context was not specified.
        /// </summary>
        public MatchInfoContext Context { get; set; }

        /// <summary>
        /// Returns the path of the matching file truncated relative to the <paramref name="directory"/> parameter.
        /// </summary>
        /// <remarks>
        /// For example, if the matching path was c:\foo\bar\baz.c and the directory argument was c:\foo
        /// the routine would return bar\baz.c .
        /// </remarks>
        /// <param name="directory">The directory base the truncation on.</param>
        /// <returns>The relative path that was produced.</returns>
        public string RelativePath(string directory)
        {
            if (!_pathSet)
            {
                return this.Path;
            }

            string relPath = _path;
            if (!string.IsNullOrEmpty(directory))
            {
                if (relPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    int offset = directory.Length;
                    if (offset < relPath.Length)
                    {
                        if (directory[offset - 1] == '\\' || directory[offset - 1] == '/')
                        {
                            relPath = relPath.Substring(offset);
                        }
                        else if (relPath[offset] == '\\' || relPath[offset] == '/')
                        {
                            relPath = relPath.Substring(offset + 1);
                        }
                    }
                }
            }

            return relPath;
        }

        private const string MatchFormat = "{0}{1}:{2}:{3}";
        private const string SimpleFormat = "{0}{1}";

        // Prefixes used by formatting: Match and Context prefixes
        // are used when context-tracking is enabled, otherwise
        // the empty prefix is used.
        private const string MatchPrefix = "> ";
        private const string ContextPrefix = "  ";
        private const string EmptyPrefix = "";

        /// <summary>
        /// Returns the string representation of this object. The format
        /// depends on whether a path has been set for this object or not.
        /// </summary>
        /// <remarks>
        /// If the path component is set, as would be the case when matching
        /// in a file, ToString() would return the path, line number and line text.
        /// If path is not set, then just the line text is presented.
        /// </remarks>
        /// <returns>The string representation of the match object.</returns>
        public override string ToString()
        {
            return ToString(null);
        }

        /// <summary>
        /// Returns the string representation of the match object same format as ToString()
        /// but trims the path to be relative to the <paramref name="directory"/> argument.
        /// </summary>
        /// <param name="directory">Directory to use as the root when calculating the relative path.</param>
        /// <returns>The string representation of the match object.</returns>
        public string ToString(string directory)
        {
            return ToString(directory, Line);
        }

        /// <summary>
        /// Returns the string representation of the match object with the matched line passed
        /// in as <paramref name="line"/> and trims the path to be relative to
        /// the<paramref name="directory"/> argument.
        /// </summary>
        /// <param name="directory">Directory to use as the root when calculating the relative path.</param>
        /// <param name="line">Line that the match occurs in.</param>
        /// <returns>The string representation of the match object.</returns>
        private string ToString(string directory, string line)
        {
            string displayPath = (directory != null) ? RelativePath(directory) : _path;

            // Just return a single line if the user didn't
            // enable context-tracking.
            if (Context == null)
            {
                return FormatLine(line, this.LineNumber, displayPath, EmptyPrefix);
            }

            // Otherwise, render the full context.
            List<string> lines = new(Context.DisplayPreContext.Length + Context.DisplayPostContext.Length + 1);

            ulong displayLineNumber = this.LineNumber - (ulong)Context.DisplayPreContext.Length;
            foreach (string contextLine in Context.DisplayPreContext)
            {
                lines.Add(FormatLine(contextLine, displayLineNumber++, displayPath, ContextPrefix));
            }

            lines.Add(FormatLine(line, displayLineNumber++, displayPath, MatchPrefix));

            foreach (string contextLine in Context.DisplayPostContext)
            {
                lines.Add(FormatLine(contextLine, displayLineNumber++, displayPath, ContextPrefix));
            }

            return string.Join(System.Environment.NewLine, lines.ToArray());
        }

        /// <summary>
        /// Returns the string representation of the match object same format as ToString()
        /// and inverts the color of the matched text if virtual terminal is supported.
        /// </summary>
        /// <param name="directory">Directory to use as the root when calculating the relative path.</param>
        /// <returns>The string representation of the match object with matched text inverted.</returns>
        public string ToEmphasizedString(string directory)
        {
            if (!_emphasize)
            {
                return ToString(directory);
            }

            return ToString(directory, EmphasizeLine());
        }

        /// <summary>
        /// Surrounds the matched text with virtual terminal sequences to invert it's color. Used in ToEmphasizedString.
        /// </summary>
        /// <returns>The matched line with matched text inverted.</returns>
        private string EmphasizeLine()
        {
            string invertColorsVT100 = PSStyle.Instance.Reverse;
            string resetVT100 = PSStyle.Instance.Reset;

            char[] chars = new char[(_matchIndexes.Count * (invertColorsVT100.Length + resetVT100.Length)) + Line.Length];
            int lineIndex = 0;
            int charsIndex = 0;
            for (int i = 0; i < _matchIndexes.Count; i++)
            {
                // Adds characters before match
                Line.CopyTo(lineIndex, chars, charsIndex, _matchIndexes[i] - lineIndex);
                charsIndex += _matchIndexes[i] - lineIndex;
                lineIndex = _matchIndexes[i];

                // Adds opening vt sequence
                invertColorsVT100.CopyTo(0, chars, charsIndex, invertColorsVT100.Length);
                charsIndex += invertColorsVT100.Length;

                // Adds characters being emphasized
                Line.CopyTo(lineIndex, chars, charsIndex, _matchLengths[i]);
                lineIndex += _matchLengths[i];
                charsIndex += _matchLengths[i];

                // Adds closing vt sequence
                resetVT100.CopyTo(0, chars, charsIndex, resetVT100.Length);
                charsIndex += resetVT100.Length;
            }

            // Adds remaining characters in line
            Line.CopyTo(lineIndex, chars, charsIndex, Line.Length - lineIndex);

            return new string(chars);
        }

        /// <summary>
        /// Formats a line for use in ToString.
        /// </summary>
        /// <param name="lineStr">The line to format.</param>
        /// <param name="displayLineNumber">The line number to display.</param>
        /// <param name="displayPath">The file path, formatted for display.</param>
        /// <param name="prefix">The match prefix.</param>
        /// <returns>The formatted line as a string.</returns>
        private string FormatLine(string lineStr, ulong displayLineNumber, string displayPath, string prefix)
        {
            return _pathSet
                       ? StringUtil.Format(MatchFormat, prefix, displayPath, displayLineNumber, lineStr)
                       : StringUtil.Format(SimpleFormat, prefix, lineStr);
        }

        /// <summary>
        /// Gets or sets a list of all Regex matches on the matching line.
        /// </summary>
        public Match[] Matches { get; set; } = Array.Empty<Match>();

        /// <summary>
        /// Create a deep copy of this MatchInfo instance.
        /// </summary>
        /// <returns>A new object that is a copy of this instance.</returns>
        internal MatchInfo Clone()
        {
            // Just do a shallow copy and then deep-copy the
            // fields that need it.
            MatchInfo clone = (MatchInfo)this.MemberwiseClone();

            if (clone.Context != null)
            {
                clone.Context = (MatchInfoContext)clone.Context.Clone();
            }

            // Regex match objects are immutable, so we can get away
            // with just copying the array.
            clone.Matches = (Match[])clone.Matches.Clone();

            return clone;
        }
    }

    /// <summary>
    /// A cmdlet to search through strings and files for particular patterns.
    /// </summary>
    [Cmdlet(VerbsCommon.Select, "String", DefaultParameterSetName = ParameterSetFile, HelpUri = "https://go.microsoft.com/fwlink/?LinkID=2097119")]
    [OutputType(typeof(bool), typeof(MatchInfo), ParameterSetName = new[] { ParameterSetFile, ParameterSetObject, ParameterSetLiteralFile })]
    [OutputType(typeof(string), ParameterSetName = new[] { ParameterSetFileRaw, ParameterSetObjectRaw, ParameterSetLiteralFileRaw })]
    public sealed class SelectStringCommand : PSCmdlet
    {
        private readonly Lock _scanCancellationLock = new();
        private CancellationTokenSource _scanCancellation;

        /// <summary>Wake file workers even when no matching output is being produced.</summary>
        protected override void StopProcessing()
        {
            lock (_scanCancellationLock)
            {
                _scanCancellation?.Cancel();
            }
        }

        private const string ParameterSetFile = "File";
        private const string ParameterSetFileRaw = "FileRaw";
        private const string ParameterSetObject = "Object";
        private const string ParameterSetObjectRaw = "ObjectRaw";
        private const string ParameterSetLiteralFile = "LiteralFile";
        private const string ParameterSetLiteralFileRaw = "LiteralFileRaw";

        /// <summary>
        /// Gets or sets a culture name.
        /// </summary>
        [Parameter]
        [ValidateSet(typeof(ValidateMatchStringCultureNamesGenerator))]
        [ValidateNotNull]
        public string Culture
        {
            get
            {
                switch (_stringComparison)
                {
                    case StringComparison.Ordinal:
                    case StringComparison.OrdinalIgnoreCase:
                        {
                            return OrdinalCultureName;
                        }

                    case StringComparison.InvariantCulture:
                    case StringComparison.InvariantCultureIgnoreCase:
                        {
                            return InvariantCultureName;
                        }

                    case StringComparison.CurrentCulture:
                    case StringComparison.CurrentCultureIgnoreCase:
                        {
                            return CurrentCultureName;
                        }

                    default:
                        {
                            break;
                        }
                }

                return _cultureName;
            }

            set
            {
                _cultureName = value;
                InitCulture();
            }
        }

        internal const string OrdinalCultureName = "Ordinal";
        internal const string InvariantCultureName = "Invariant";
        internal const string CurrentCultureName = "Current";

        private string _cultureName = CultureInfo.CurrentCulture.Name;
        private StringComparison _stringComparison = StringComparison.CurrentCultureIgnoreCase;
        private CompareOptions _compareOptions = CompareOptions.IgnoreCase;

        private CompareInfo _compareInfo = CultureInfo.CurrentCulture.CompareInfo;

        private void InitCulture()
        {
            _stringComparison = default;

            switch (_cultureName)
            {
                case OrdinalCultureName:
                    {
                        _stringComparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        _compareOptions = CaseSensitive ? CompareOptions.Ordinal : CompareOptions.OrdinalIgnoreCase;
                        _compareInfo = CultureInfo.InvariantCulture.CompareInfo;
                        break;
                    }

                case InvariantCultureName:
                    {
                        _stringComparison = CaseSensitive ? StringComparison.InvariantCulture : StringComparison.InvariantCultureIgnoreCase;
                        _compareOptions = CaseSensitive ? CompareOptions.None : CompareOptions.IgnoreCase;
                        _compareInfo = CultureInfo.InvariantCulture.CompareInfo;
                        break;
                    }

                case CurrentCultureName:
                    {
                        _stringComparison = CaseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
                        _compareOptions = CaseSensitive ? CompareOptions.None : CompareOptions.IgnoreCase;
                        _compareInfo = CultureInfo.CurrentCulture.CompareInfo;
                        break;
                    }

                default:
                    {
                        var _cultureInfo = CultureInfo.GetCultureInfo(_cultureName);
                        _compareOptions = CaseSensitive ? CompareOptions.None : CompareOptions.IgnoreCase;
                        _compareInfo = _cultureInfo.CompareInfo;
                        break;
                    }
            }
        }

        /// <summary>
        /// Gets or sets the current pipeline object.
        /// </summary>
        [Parameter(ValueFromPipeline = true, Mandatory = true, ParameterSetName = ParameterSetObject)]
        [Parameter(ValueFromPipeline = true, Mandatory = true, ParameterSetName = ParameterSetObjectRaw)]
        [AllowNull]
        [AllowEmptyString]
        public PSObject InputObject
        {
            get => _inputObject;
            set => _inputObject = LanguagePrimitives.IsNull(value) ? PSObject.AsPSObject(string.Empty) : value;
        }

        private PSObject _inputObject = AutomationNull.Value;

        /// <summary>
        /// Gets or sets the patterns to find.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0)]
        public string[] Pattern { get; set; }

        private SelectStringEngine _engine;

        /// <summary>
        /// Gets or sets files to read from.
        /// Globbing is done on these.
        /// </summary>
        [Parameter(Position = 1, Mandatory = true, ValueFromPipelineByPropertyName = true, ParameterSetName = ParameterSetFile)]
        [Parameter(Position = 1, Mandatory = true, ValueFromPipelineByPropertyName = true, ParameterSetName = ParameterSetFileRaw)]
        [FileinfoToString]
        public string[] Path { get; set; }

        /// <summary>
        /// Gets or sets literal files to read from.
        /// Globbing is not done on these.
        /// </summary>
        [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true, ParameterSetName = ParameterSetLiteralFile)]
        [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true, ParameterSetName = ParameterSetLiteralFileRaw)]
        [FileinfoToString]
        [Alias("PSPath", "LP")]
        public string[] LiteralPath
        {
            get => Path;
            set
            {
                Path = value;
                _isLiteralPath = true;
            }
        }

        private bool _isLiteralPath;

        /// <summary>
        /// Gets or sets a value indicating if only string values containing matched lines should be returned.
        /// If not (default) return MatchInfo (or bool objects, when Quiet is passed).
        /// </summary>
        [Parameter(Mandatory = true, ParameterSetName = ParameterSetObjectRaw)]
        [Parameter(Mandatory = true, ParameterSetName = ParameterSetFileRaw)]
        [Parameter(Mandatory = true, ParameterSetName = ParameterSetLiteralFileRaw)]
        public SwitchParameter Raw { get; set; }

        /// <summary>
        /// Gets or sets a value indicating if a pattern string should be matched literally.
        /// If not (default) search using pattern as a Regular Expression.
        /// </summary>
        [Parameter]
        public SwitchParameter SimpleMatch { get; set; }

        /// <summary>
        /// Gets or sets a value indicating if the search is case sensitive.If true, then do case-sensitive searches.
        /// </summary>
        [Parameter]
        public SwitchParameter CaseSensitive { get; set; }

        /// <summary>
        /// Gets or sets a value indicating if the cmdlet will stop processing at the first successful match and
        /// return true.  If both List and Quiet parameters are given, an exception is thrown.
        /// </summary>
        [Parameter(ParameterSetName = ParameterSetObject)]
        [Parameter(ParameterSetName = ParameterSetFile)]
        [Parameter(ParameterSetName = ParameterSetLiteralFile)]
        public SwitchParameter Quiet { get; set; }

        /// <summary>
        /// Gets or sets a value indicating if matching files should be listed.
        /// This is the Unix functionality this switch is intended to mimic;
        /// the actual action of this option is to stop after the first match
        /// is found and returned from any particular file.
        /// </summary>
        [Parameter]
        public SwitchParameter List { get; set; }

        /// <summary>
        /// Gets or sets a value indicating if highlighting should be disabled.
        /// </summary>
        [Parameter]
        public SwitchParameter NoEmphasis { get; set; }

        /// <summary>
        /// Gets or sets files to include. Files matching
        /// one of these (if specified) are included.
        /// </summary>
        /// <exception cref="WildcardPatternException">Invalid wildcard pattern was specified.</exception>
        [Parameter]
        [ValidateNotNullOrEmpty]
        public string[] Include
        {
            get => _includeStrings;
            set
            {
                // null check is not needed (because of ValidateNotNullOrEmpty),
                // but we have to include it to silence OACR
                _includeStrings = value ?? throw PSTraceSource.NewArgumentNullException(nameof(value));

                _include = new WildcardPattern[_includeStrings.Length];
                for (int i = 0; i < _includeStrings.Length; i++)
                {
                    _include[i] = WildcardPattern.Get(_includeStrings[i], WildcardOptions.IgnoreCase);
                }
            }
        }

        private string[] _includeStrings;

        private WildcardPattern[] _include;

        /// <summary>
        /// Gets or sets files to exclude. Files matching
        /// one of these (if specified) are excluded.
        /// </summary>
        [Parameter]
        [ValidateNotNullOrEmpty]
        public string[] Exclude
        {
            get => _excludeStrings;
            set
            {
                // null check is not needed (because of ValidateNotNullOrEmpty),
                // but we have to include it to silence OACR
                _excludeStrings = value ?? throw PSTraceSource.NewArgumentNullException("value");

                _exclude = new WildcardPattern[_excludeStrings.Length];
                for (int i = 0; i < _excludeStrings.Length; i++)
                {
                    _exclude[i] = WildcardPattern.Get(_excludeStrings[i], WildcardOptions.IgnoreCase);
                }
            }
        }

        private string[] _excludeStrings;

        private WildcardPattern[] _exclude;

        /// <summary>
        /// Gets or sets a value indicating whether to only show lines which do not match.
        /// Equivalent to grep -v/findstr -v.
        /// </summary>
        [Parameter]
        public SwitchParameter NotMatch { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Matches property of MatchInfo should be set
        /// to the result of calling System.Text.RegularExpressions.Regex.Matches() on
        /// the corresponding line.
        /// Has no effect if -SimpleMatch is also specified.
        /// </summary>
        [Parameter]
        public SwitchParameter AllMatches { get; set; }

        /// <summary>
        /// Gets or sets the text encoding to process each file as.
        /// </summary>
        [Parameter]
        [ArgumentToEncodingTransformation]
        [ArgumentEncodingCompletions]
        [ValidateNotNullOrEmpty]
        public Encoding Encoding
        {
            get
            {
                return _encoding;
            }

            set
            {
                EncodingConversion.WarnIfObsolete(this, value);
                _encoding = value;
            }
        }

        private Encoding _encoding = Encoding.Default;

        /// <summary>
        /// Gets or sets the number of context lines to collect. If set to a
        /// single integer value N, collects N lines each of pre-
        /// and post- context. If set to a 2-tuple B,A, collects B
        /// lines of pre- and A lines of post- context.
        /// If set to a list with more than 2 elements, the
        /// excess elements are ignored.
        /// </summary>
        [Parameter]
        [ValidateNotNullOrEmpty]
        [ValidateCount(1, 2)]
        [ValidateRange(0, int.MaxValue)]
        public new int[] Context
        {
            get => _context;
            set
            {
                // null check is not needed (because of ValidateNotNullOrEmpty),
                // but we have to include it to silence OACR
                _context = value ?? throw PSTraceSource.NewArgumentNullException("value");

                if (_context.Length == 1)
                {
                    _preContext = _context[0];
                    _postContext = _context[0];
                }
                else if (_context.Length >= 2)
                {
                    _preContext = _context[0];
                    _postContext = _context[1];
                }
            }
        }

        private int[] _context;

        private int _preContext = 0;

        private int _postContext = 0;

        private bool LightweightOutput => Raw || (Quiet && !List);

        // Pipeline context persists across records; files use their own trackers.
        private SelectStringEngine.IContextTracker<MatchInfo> _matchContextTracker;
        private SelectStringEngine.IContextTracker<SearchResult> _lightContextTracker;

        /// <summary>
        /// This is used to handle the case were we're done processing input objects.
        /// If true, process record will just return.
        /// </summary>
        private bool _doneProcessing;

        private ulong _inputRecordNumber;

        /// <summary>
        /// Read command line parameters.
        /// </summary>
        protected override void BeginProcessing()
        {
            if (this.MyInvocation.BoundParameters.ContainsKey(nameof(Culture)) && !this.MyInvocation.BoundParameters.ContainsKey(nameof(SimpleMatch)))
            {
                InvalidOperationException exception = new(MatchStringStrings.CannotSpecifyCultureWithoutSimpleMatch);
                ErrorRecord errorRecord = new(exception, "CannotSpecifyCultureWithoutSimpleMatch", ErrorCategory.InvalidData, null);
                this.ThrowTerminatingError(errorRecord);
            }

            InitCulture();

            string suppressVt = Environment.GetEnvironmentVariable("__SuppressAnsiEscapeSequences");
            if (!string.IsNullOrEmpty(suppressVt))
            {
                NoEmphasis = true;
            }

            try
            {
                // Reuse the compiled patterns across every record and file in this
                // invocation. SimpleMatch does not construct regular expressions.
                _engine = new SelectStringEngine(Pattern, SimpleMatch, CaseSensitive, AllMatches, NotMatch, _compareInfo, _compareOptions, compiledRegex: true);
            }
            catch (SelectStringEngine.InvalidPatternException e)
            {
                Exception cause = e.InnerException;
                ThrowTerminatingError(BuildErrorRecord(MatchStringStrings.InvalidRegex, Pattern[e.PatternIndex], cause.Message, "InvalidRegex", cause));
                throw;
            }

            if (LightweightOutput)
            {
                _lightContextTracker = SelectStringEngine.CreateContextTracker<SearchResult, LightweightFactory>(
                    Raw ? 0 : _preContext, Raw ? 0 : _postContext, new LightweightFactory(this));
            }
            else
            {
                _matchContextTracker = SelectStringEngine.CreateContextTracker<MatchInfo, MatchInfoFactory>(
                    _preContext, _postContext, new MatchInfoFactory(this));
            }
        }

        private readonly List<string> _inputObjectFileList = new(1) { string.Empty };

        /// <summary>
        /// Process the input.
        /// </summary>
        /// <exception cref="ArgumentException">Regular expression parsing error, path error.</exception>
        /// <exception cref="FileNotFoundException">A file cannot be found.</exception>
        /// <exception cref="DirectoryNotFoundException">A file cannot be found.</exception>
        protected override void ProcessRecord()
        {
            if (_doneProcessing)
            {
                return;
            }

            // We may only have directories when we have resolved wildcards
            var expandedPathsMaybeDirectory = false;
            List<string> expandedPaths = null;
            if (Path != null)
            {
                expandedPaths = ResolveFilePaths(Path, _isLiteralPath);
                if (expandedPaths == null)
                {
                    return;
                }

                expandedPathsMaybeDirectory = true;
            }
            else
            {
                if (_inputObject.BaseObject is FileInfo fileInfo)
                {
                    _inputObjectFileList[0] = fileInfo.FullName;
                    expandedPaths = _inputObjectFileList;
                }
            }

            if (expandedPaths != null)
            {
                if (CanScanFilesConcurrently(expandedPaths))
                {
                    ProcessFilesConcurrently(expandedPaths);
                    return;
                }

                foreach (var filename in expandedPaths)
                {
                    if (expandedPathsMaybeDirectory && Directory.Exists(filename))
                    {
                        continue;
                    }

                    var foundMatch = ProcessFile(filename);
                    if (Quiet && foundMatch)
                    {
                        return;
                    }
                }

                // No results in any files.
                if (Quiet)
                {
                    var res = List ? null : Boxed.False;
                    WriteObject(res);
                }
            }
            else
            {
                if (LightweightOutput)
                {
                    ProcessInputRecord(ref _lightContextTracker, new LightweightFactory(this));
                }
                else
                {
                    ProcessInputRecord(ref _matchContextTracker, new MatchInfoFactory(this));
                }
            }
        }

        private void ProcessInputRecord<T, TFactory>(ref SelectStringEngine.IContextTracker<T> tracker, TFactory factory)
            where T : class
            where TFactory : struct, IOutputFactory<T>
        {
            _inputRecordNumber++;
            MatchInfo original = _inputObject.BaseObject as MatchInfo;
            string line;
            if (original != null)
            {
                line = original.Line;
                if (_preContext > 0 || _postContext > 0)
                {
                    _preContext = 0;
                    _postContext = 0;
                    // Deliberately retain a zero-width context tracker here: the
                    // shipped filter clears logical as well as display context.
                    tracker = new SelectStringEngine.ContextTracker<T, TFactory>(0, 0, factory);
                    WarnFilterContext();
                }
            }
            else
            {
                line = _inputObject.BaseObject as string
                    ?? (string)LanguagePrimitives.ConvertTo(_inputObject, typeof(string), CultureInfo.InvariantCulture);
            }

            if (_engine.TryMatch(line, out var match))
            {
                T result;
                if (original != null)
                {
                    if (original.Context != null)
                    {
                        original = original.Clone();
                        original.Context.DisplayPreContext = Array.Empty<string>();
                        original.Context.DisplayPostContext = Array.Empty<string>();
                    }

                    result = factory.FromExisting(original);
                }
                else
                {
                    result = factory.Create(line, null, _inputRecordNumber, in match, _preContext > 0 || _postContext > 0);
                }

                tracker.TrackMatch(result);
            }
            else
            {
                tracker.TrackLine(line);
            }

            if (FlushTrackerQueue(tracker, factory) && Quiet)
            {
                _doneProcessing = true;
            }
        }

        /// <summary>
        /// Process a file which was either specified on the
        /// command line or passed in as a FileInfo object.
        /// </summary>
        /// <param name="filename">The file to process.</param>
        /// <returns>True if a match was found; otherwise false.</returns>
        private bool ProcessFile(string filename)
        {
            try
            {
                if (!MeetsIncludeExcludeCriteria(filename))
                {
                    return false;
                }

                return LightweightOutput
                    ? ScanFile<SearchResult, LightweightFactory>(filename, new LightweightFactory(this))
                    : ScanFile<MatchInfo, MatchInfoFactory>(filename, new MatchInfoFactory(this));
            }
            catch (NotSupportedException nse)
            {
                WriteError(BuildErrorRecord(MatchStringStrings.FileReadError, filename, nse.Message, "ProcessingFile", nse));
            }
            catch (IOException ioe)
            {
                WriteError(BuildErrorRecord(MatchStringStrings.FileReadError, filename, ioe.Message, "ProcessingFile", ioe));
            }
            catch (System.Security.SecurityException se)
            {
                WriteError(BuildErrorRecord(MatchStringStrings.FileReadError, filename, se.Message, "ProcessingFile", se));
            }
            catch (UnauthorizedAccessException uae)
            {
                WriteError(BuildErrorRecord(MatchStringStrings.FileReadError, filename, uae.Message, "ProcessingFile", uae));
            }

            return false;
        }

        private bool CanScanFilesConcurrently(List<string> paths)
        {
            // Keep short-circuit modes and individually arriving pipeline objects on
            // their original path. Custom/strict decoders may observe speculative reads.
            // Limit this first integration to ordinary Windows files; Unix special
            // files need a reliable file-type check before opening on worker threads.
            return OperatingSystem.IsWindows() && Path != null && paths.Count > 1 &&
                !Quiet && !List && !Raw && Encoding is UTF8Encoding &&
                Encoding.GetType().Assembly == typeof(UTF8Encoding).Assembly &&
                Encoding.DecoderFallback is DecoderReplacementFallback &&
                paths.TrueForAll(IsConcurrentScanFile);
        }

        private static bool IsConcurrentScanFile(string path)
        {
            try
            {
                return File.Exists(path) &&
                    (File.GetAttributes(path) & (FileAttributes.Device | FileAttributes.ReparsePoint)) == 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Let the sequential path report metadata/open failures with its
                // existing error identity and ordering, including file-removal races.
                return false;
            }
        }

        private void ProcessFilesConcurrently(List<string> paths)
        {
            // Resolve/filter on the pipeline thread. Workers receive only file names,
            // engine settings and a token; they never read command or host state.
            List<string> included = paths.FindAll(MeetsIncludeExcludeCriteria);
            var factory = new MatchInfoFactory(this);
            using var cancellation = new CancellationTokenSource();
            lock (_scanCancellationLock)
            {
                _scanCancellation = cancellation;
                if (Stopping) { cancellation.Cancel(); }
            }

            try
            {
                _engine.ScanFiles(included, Encoding, result =>
                {
                    if (Stopping) { return false; }
                    var data = new SelectStringEngine.MatchData(result.PatternIndex, result.SimpleMatchIndex, result.Matches);
                    MatchInfo output = factory.Create(result.Line, result.Path, result.LineNumber, in data, result.Context != null);
                    if (result.Context is { } context)
                    {
                        factory.SetLogicalContext(output, context.PreContext, context.PostContext);
                        factory.SetDisplayContext(output, context.DisplayPreContext, context.DisplayPostContext);
                    }

                    factory.Write(output);
                    return true;
                }, (filename, error) =>
                {
                    if (error is NotSupportedException or IOException or System.Security.SecurityException or UnauthorizedAccessException)
                    {
                        WriteError(BuildErrorRecord(MatchStringStrings.FileReadError, filename, error.Message, "ProcessingFile", error));
                    }
                    else
                    {
                        ExceptionDispatchInfo.Capture(error).Throw();
                    }
                }, _preContext, _postContext, cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException) when (Stopping)
            {
                // Pipeline stop already owns termination; workers have been joined.
            }
            finally
            {
                // StopProcessing may run concurrently with this cleanup. Remove the
                // published token source under the same lock before disposing it.
                lock (_scanCancellationLock)
                {
                    _scanCancellation = null;
                }
            }
        }

        private bool ScanFile<T, TFactory>(string filename, TFactory factory)
            where T : class
            where TFactory : struct, IOutputFactory<T>
        {
            return _engine.ScanFile<T, TFactory>(
                filename, Encoding, Raw ? 0 : _preContext, Raw ? 0 : _postContext,
                Quiet || List, factory,
                result =>
                {
                    if (Stopping)
                    {
                        return false;
                    }

                    factory.Write(result);
                    return true;
                },
                shouldStop: () => Stopping);
        }

        private bool FlushTrackerQueue<T, TFactory>(SelectStringEngine.IContextTracker<T> tracker, TFactory factory)
            where T : class
            where TFactory : struct, IOutputFactory<T>
        {
            if (tracker.EmitQueue.Count == 0)
            {
                return false;
            }

            if (Quiet || List)
            {
                factory.Write(tracker.EmitQueue[0]);
            }
            else
            {
                foreach (T result in tracker.EmitQueue)
                {
                    factory.Write(result);
                }
            }

            tracker.EmitQueue.Clear();
            return true;
        }

        /// <summary>
        /// Emits matches still awaiting context when pipeline input ends.
        /// </summary>
        protected override void EndProcessing()
        {
            if (LightweightOutput)
            {
                _lightContextTracker.TrackEOF();
                if (!_doneProcessing)
                {
                    FlushTrackerQueue(_lightContextTracker, new LightweightFactory(this));
                }
            }
            else
            {
                _matchContextTracker.TrackEOF();
                if (!_doneProcessing)
                {
                    FlushTrackerQueue(_matchContextTracker, new MatchInfoFactory(this));
                }
            }
        }

        private interface IOutputFactory<T> : SelectStringEngine.IResultFactory<T> where T : class
        {
            T FromExisting(MatchInfo value);

            void Write(T result);
        }

        private readonly struct MatchInfoFactory(SelectStringCommand command) : IOutputFactory<MatchInfo>
        {
            private readonly bool _emphasize = !command.NoEmphasis && command.Host.UI.SupportsVirtualTerminal;

            public MatchInfo Create(string line, string path, ulong lineNumber, in SelectStringEngine.MatchData data, bool context)
            {
                MatchInfo result;
                if (_emphasize)
                {
                    List<int> indexes = new();
                    List<int> lengths = new();
                    if (data.SimpleMatchIndex >= 0)
                    {
                        indexes.Add(data.SimpleMatchIndex);
                        lengths.Add(command.Pattern[data.PatternIndex].Length);
                    }
                    else
                    {
                        foreach (Match match in data.Matches)
                        {
                            indexes.Add(match.Index);
                            lengths.Add(match.Length);
                        }
                    }

                    result = new MatchInfo(indexes, lengths);
                }
                else
                {
                    result = new MatchInfo();
                }

                result.IgnoreCase = !command.CaseSensitive;
                result.Line = line;
                result.LineNumber = lineNumber;
                result.Pattern = command.Pattern[data.PatternIndex];
                result.Matches = data.Matches;
                if (path != null)
                {
                    result.Path = path;
                }

                if (context)
                {
                    result.Context = new MatchInfoContext();
                }

                return result;
            }

            public MatchInfo FromExisting(MatchInfo value) => value;

            public string GetLine(MatchInfo result) => result.Line;

            public bool HasContext(MatchInfo result) => result.Context != null;

            public void SetLogicalContext(MatchInfo result, string[] before, string[] after)
            {
                result.Context.PreContext = before;
                result.Context.PostContext = after;
            }

            public void SetDisplayContext(MatchInfo result, string[] before, string[] after)
            {
                result.Context.DisplayPreContext = before;
                result.Context.DisplayPostContext = after;
            }

            public void Write(MatchInfo result) => command.WriteObject(result);
        }

        private readonly struct LightweightFactory(SelectStringCommand command) : IOutputFactory<SearchResult>
        {
            public SearchResult Create(string line, string path, ulong lineNumber, in SelectStringEngine.MatchData data, bool context) =>
                default(SelectStringEngine.ResultFactory).Create(line, path, lineNumber, in data, context);

            public SearchResult FromExisting(MatchInfo value) => new()
            {
                Line = value.Line,
                LineNumber = value.LineNumber,
                Context = value.Context == null ? null : new SelectStringEngine.ResultContext
                {
                    PreContext = value.Context.PreContext,
                    PostContext = value.Context.PostContext,
                    DisplayPreContext = value.Context.DisplayPreContext,
                    DisplayPostContext = value.Context.DisplayPostContext,
                },
            };

            public string GetLine(SearchResult result) => result.Line;

            public bool HasContext(SearchResult result) => result.Context != null;

            public void SetLogicalContext(SearchResult result, string[] before, string[] after) =>
                default(SelectStringEngine.ResultFactory).SetLogicalContext(result, before, after);

            public void SetDisplayContext(SearchResult result, string[] before, string[] after) =>
                default(SelectStringEngine.ResultFactory).SetDisplayContext(result, before, after);

            public void Write(SearchResult result) => command.WriteObject(command.Raw ? (object)result.Line : true);
        }

        /// <summary>
        /// Get a list or resolved file paths.
        /// </summary>
        /// <param name="filePaths">The filePaths to resolve.</param>
        /// <param name="isLiteralPath">True if the wildcard resolution should not be attempted.</param>
        /// <returns>The resolved (absolute) paths.</returns>
        private List<string> ResolveFilePaths(string[] filePaths, bool isLiteralPath)
        {
            List<string> allPaths = new();

            foreach (string path in filePaths)
            {
                Collection<string> resolvedPaths;
                ProviderInfo provider;
                if (isLiteralPath)
                {
                    resolvedPaths = new Collection<string>();
                    string resolvedPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(path, out provider, out _);
                    resolvedPaths.Add(resolvedPath);
                }
                else
                {
                    resolvedPaths = GetResolvedProviderPathFromPSPath(path, out provider);
                }

                if (!provider.NameEquals(base.Context.ProviderNames.FileSystem))
                {
                    // "The current provider ({0}) cannot open a file"
                    WriteError(BuildErrorRecord(MatchStringStrings.FileOpenError, provider.FullName, "ProcessingFile", null));
                    continue;
                }

                allPaths.AddRange(resolvedPaths);
            }

            return allPaths;
        }

        private static ErrorRecord BuildErrorRecord(string messageId, string argument, string errorId, Exception innerException)
        {
            return BuildErrorRecord(messageId, new object[] { argument }, errorId, innerException);
        }

        private static ErrorRecord BuildErrorRecord(string messageId, string arg0, string arg1, string errorId, Exception innerException)
        {
            return BuildErrorRecord(messageId, new object[] { arg0, arg1 }, errorId, innerException);
        }

        private static ErrorRecord BuildErrorRecord(string messageId, object[] arguments, string errorId, Exception innerException)
        {
            string fmtedMsg = StringUtil.Format(messageId, arguments);
            ArgumentException e = new(fmtedMsg, innerException);
            return new ErrorRecord(e, errorId, ErrorCategory.InvalidArgument, null);
        }

        private void WarnFilterContext()
        {
            string msg = MatchStringStrings.FilterContextWarning;
            WriteWarning(msg);
        }

        /// <summary>
        /// Magic class that works around the limitations on ToString() for FileInfo.
        /// </summary>
        private sealed class FileinfoToStringAttribute : ArgumentTransformationAttribute
        {
            public override object Transform(EngineIntrinsics engineIntrinsics, object inputData)
            {
                object result = inputData;

                if (result is PSObject mso)
                {
                    result = mso.BaseObject;
                }

                FileInfo fileInfo;

                // Handle an array of elements...
                if (result is IList argList)
                {
                    object[] resultList = new object[argList.Count];

                    for (int i = 0; i < argList.Count; i++)
                    {
                        object element = argList[i];

                        mso = element as PSObject;
                        if (mso != null)
                        {
                            element = mso.BaseObject;
                        }

                        fileInfo = element as FileInfo;
                        resultList[i] = fileInfo?.FullName ?? element;
                    }

                    return resultList;
                }

                // Handle the singleton case...
                fileInfo = result as FileInfo;
                if (fileInfo != null)
                {
                    return fileInfo.FullName;
                }

                return inputData;
            }
        }

        /// <summary>
        /// Check whether the supplied name meets the include/exclude criteria.
        /// That is - it's on the include list if there is one and not on
        /// the exclude list if there was one of those.
        /// </summary>
        /// <param name="filename">The filename to test.</param>
        /// <returns>True if the filename is acceptable.</returns>
        private bool MeetsIncludeExcludeCriteria(string filename)
        {
            bool ok = false;

            // see if the file is on the include list...
            if (_include != null)
            {
                foreach (WildcardPattern patternItem in _include)
                {
                    if (patternItem.IsMatch(filename))
                    {
                        ok = true;
                        break;
                    }
                }
            }
            else
            {
                ok = true;
            }

            if (!ok)
            {
                return false;
            }

            // now see if it's on the exclude list...
            if (_exclude != null)
            {
                foreach (WildcardPattern patternItem in _exclude)
                {
                    if (patternItem.IsMatch(filename))
                    {
                        ok = false;
                        break;
                    }
                }
            }

            return ok;
        }
    }

    /// <summary>
    /// Get list of valid culture names for ValidateSet attribute.
    /// </summary>
    public class ValidateMatchStringCultureNamesGenerator : IValidateSetValuesGenerator
    {
        string[] IValidateSetValuesGenerator.GetValidValues()
        {
            var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
            var result = new List<string>(cultures.Length + 3);
            result.Add(SelectStringCommand.OrdinalCultureName);
            result.Add(SelectStringCommand.InvariantCultureName);
            result.Add(SelectStringCommand.CurrentCultureName);
            foreach (var cultureInfo in cultures)
            {
                result.Add(cultureInfo.Name);
            }

            return result.ToArray();
        }
    }
}

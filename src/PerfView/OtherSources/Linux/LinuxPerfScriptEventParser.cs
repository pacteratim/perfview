﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PerfView.Utilities;

namespace Diagnostics.Tracing.StackSources
{
    public class LinuxPerfScriptEventParser
    {
        public LinuxPerfScriptEventParser()
        {
            this.mapper = null;
            this.SetDefaultValues();
        }

        /// <summary>
        /// Gets an estimated total number of samples created - not thread safe.
        /// </summary>
        public int EventCount { get; private set; }

        /// <summary>
        /// Tries to skip the byte order marks at the beginning of the given fast stream.
        /// </summary>
        public void SkipPreamble(FastStream source)
        {
            source.MoveNext();      // Prime Current 

            // These are bytes put at the begining of a UTF8 file (like the byte order mark (BOM)) that should be skipped.  
            var preambleBytes = Encoding.UTF8.GetPreamble();
            while (preambleBytes.Contains(source.Current))
                source.MoveNext();

            // Skip whitespace and comments.   
            for (;;)
            {
                if (Char.IsWhiteSpace((char)source.Current))
                    source.MoveNext();
                else if (source.Current == '#')
                    source.SkipUpTo('\n');
                else
                    break;
            }
        }

        /// <summary>
        /// Parses the given Linux sample data, returning one sample at a time, and
        /// automatically skips the BOM at the beginning of files.
        /// </summary>
        public IEnumerable<LinuxEvent> ParseSkippingPreamble(string filename)
        {
            return this.ParseSkippingPreamble(new FastStream(filename));
        }

        public IEnumerable<LinuxEvent> ParseSkippingPreamble(Stream stream)
        {
            return this.ParseSkippingPreamble(new FastStream(stream));
        }

        public IEnumerable<LinuxEvent> ParseSkippingPreamble(FastStream source)
        {
            this.SkipPreamble(source);

            return this.Parse(source);
        }

        /// <summary>
        /// Parse the given Linux sample data, returning one sample at a time, does not try to
        /// skip through the BOM.
        /// </summary>
        public IEnumerable<LinuxEvent> Parse(FastStream source)
        {
            if (source.Current == 0 && !source.EndOfStream)
            {
                source.MoveNext();
            }

            Regex rgx = this.Pattern;
            foreach (LinuxEvent linuxEvent in this.NextEvent(rgx, source))
            {
                if (linuxEvent != null)
                {
                    this.EventCount++; // Needs to be thread safe
                    yield return linuxEvent;
                }

                if (this.EventCount > this.MaxSamples)
                {
                    break;
                }
            }

            yield break;
        }

        /// <summary>
        /// Regex string pattern for filtering events.
        /// </summary>
        public Regex Pattern { get; set; }

        /// <summary>
        /// The amount of samples the parser takes.
        /// </summary>
        public long MaxSamples { get; set; }

        /// <summary>
        /// Uses the archive as a resource for symbol resolution when parsing Linux samples.
        /// </summary>
        public void SetSymbolFile(ZipArchive archive)
        {
            this.mapper = new LinuxPerfScriptMapper(archive, this);
        }

        /// <summary>
        /// Uses the path to open an archive with symbol files that are then used for symbol resolution when
        /// parsing Linux samples.
        /// </summary>
        public void SetSymbolFile(string path)
        {
            this.SetSymbolFile(ZipFile.OpenRead(path));
        }

        /// <summary>
        /// Parses a Microsoft symbol as shown on the Linux sample. "entireSymbol" represents the module contract between
        /// the memory address and the dll path on the Linux sample.
        /// "mapFileLocation" is the path to the dll given by the Linux sample.
        /// </summary>
        public string[] GetSymbolFromMicrosoftMap(string entireSymbol, string mapFileLocation = "")
        {
            for (int first = 0; first < entireSymbol.Length;)
            {
                int last = entireSymbol.IndexOf(' ', first);
                if (last == -1)
                {
                    last = entireSymbol.Length;
                }

                if (entireSymbol[first] == '[' && entireSymbol[last - 1] == ']')
                {
                    var symbol = entireSymbol.Substring(Math.Min(entireSymbol.Length, last + 1));
                    return new string[2] { entireSymbol.Substring(first + 1, last - first - 2), symbol.Trim() };
                }

                first = last + 1;
            }

            return new string[2] { entireSymbol, mapFileLocation };
        }

        public bool IsEndOfSample(FastStream source)
        {
            return this.IsEndOfSample(source, source.Current, source.Peek(1));
        }

        public bool IsEndOfSample(FastStream source, byte current, byte peek1)
        {
            return (current == '\n' && (peek1 == '\n' || peek1 == '\r' || peek1 == 0)) || current == 0 || source.EndOfStream;
        }

        /// <summary>
        /// Given a stream with the symbols, this function parses the stream and stores the contents in the given mapper
        /// </summary>
        public void ParseSymbolFile(Stream stream, Mapper mapper)
        {
            FastStream source = new FastStream(stream);
            source.MoveNext(); // Prime Current.  
            this.SkipPreamble(source); // Remove encoding stuff if it's there
            source.SkipWhiteSpace();

            StringBuilder sb = new StringBuilder();

            Func<byte, bool> untilWhiteSpace = (byte c) => { return !char.IsWhiteSpace((char)c); };

            while (!source.EndOfStream)
            {
                source.ReadAsciiStringUpToTrue(sb, untilWhiteSpace);
                ulong start = ulong.Parse(sb.ToString(), System.Globalization.NumberStyles.HexNumber);
                sb.Clear();
                source.SkipWhiteSpace();

                source.ReadAsciiStringUpToTrue(sb, untilWhiteSpace);
                ulong size = ulong.Parse(sb.ToString(), System.Globalization.NumberStyles.HexNumber);
                sb.Clear();
                source.SkipWhiteSpace();

                source.ReadAsciiStringUpTo('\n', sb);
                string symbol = sb.ToString().TrimEnd();
                sb.Clear();

                mapper.Add(start, size, symbol);

                source.SkipWhiteSpace();
            }
        }

        /// <summary>
        /// Given a stream that contains PerfInfo commands, parses the stream and stores data in the given dictionary.
        /// Key: somedll.ni.dll		Value: {some guid}
        /// </summary>
        public void ParsePerfInfoFile(Stream stream, Dictionary<string, string> guids)
        {
            FastStream source = new FastStream(stream);
            source.MoveNext();
            source.SkipWhiteSpace();

            StringBuilder sb = new StringBuilder();

            while (!source.EndOfStream)
            {
                source.ReadAsciiStringUpTo(';', sb);
                source.MoveNext();
                string command = sb.ToString();
                sb.Clear();

                if (command == "ImageLoad") // TODO: should be a constant maybe?
                {
                    source.ReadAsciiStringUpTo(';', sb);
                    string path = sb.ToString();
                    sb.Clear();
                    source.MoveNext();

                    source.ReadAsciiStringUpTo(';', sb);
                    string guid = sb.ToString().TrimEnd();
                    sb.Clear();

                    guids[GetFileName(path)] = guid;
                }

                source.SkipUpTo('\n');
                source.MoveNext();
            }
        }

        #region private

        /// <summary>
        /// Can't use Path.GetFileName because it fails on illegal Linux file characters.  
        /// Can remove when this changes. 
        /// </summary>
        internal static string GetFileName(string path)
        {
            var index = path.LastIndexOfAny(pathSeparators);
            if (index < 0)
                return path;
            return path.Substring(index + 1);
        }
        static char[] pathSeparators = new char[] { '/', '\\' };

        internal static string GetFileNameWithoutExtension(string path)
        {
            var start = path.LastIndexOfAny(pathSeparators);
            if (start < 0)
                start = 0;
            else
                start++;

            var end = path.LastIndexOf('.');
            if (end < start)
                end = path.Length;
            return path.Substring(start, end - start);
        }


        private void SetDefaultValues()
        {
            this.EventCount = 0;
            this.Pattern = null;
            this.MaxSamples = 50000;
        }

        private IEnumerable<LinuxEvent> NextEvent(Regex regex, FastStream source)
        {

            string line = string.Empty;

            while (true)
            {
                source.SkipWhiteSpace();

                if (source.EndOfStream)
                {
                    break;
                }

                EventKind eventKind = EventKind.Cpu;

                StringBuilder sb = new StringBuilder();

                // Command - Stops at first number AFTER first word(non-whitespace) and whitespace
                for (;;)
                {
                    if (sb.Length != 0)
                    {
                        if (this.IsNumberChar((char)source.Current))
                            break;
                        sb.Append(' ');
                    }
                    source.ReadAsciiStringUpToTrue(sb, delegate (byte c)
                    {
                        return !char.IsWhiteSpace((char)c);
                    });
                    source.SkipWhiteSpace();
                }

                string comm = sb.ToString();
                sb.Clear();

                // Process ID
                int pid = source.ReadInt();
                source.MoveNext(); // Move past the "/"

                // Thread ID
                int tid = source.ReadInt();

                // CPU
                source.SkipWhiteSpace();
                source.MoveNext(); // Move past the "["
                int cpu = source.ReadInt();
                source.MoveNext(); // Move past the "]"

                // Time
                source.SkipWhiteSpace();
                source.ReadAsciiStringUpTo(':', sb);

                double time = double.Parse(sb.ToString(), CultureInfo.InvariantCulture) * 1000; // To convert to MSec
                sb.Clear();
                source.MoveNext(); // Move past ":"

                // Time Property
                source.SkipWhiteSpace();
                int timeProp = -1;
                if (this.IsNumberChar((char)source.Current))
                {
                    timeProp = source.ReadInt();
                }

                // Event Name
                source.SkipWhiteSpace();
                source.ReadAsciiStringUpTo(':', sb);
                string eventName = sb.ToString();
                sb.Clear();
                source.MoveNext();

                // Event Properties
                // I mark a position here because I need to check what type of event this is without screwing up the stream
                var markedPosition = source.MarkPosition();
                source.ReadAsciiStringUpTo('\n', sb);
                string eventDetails = sb.ToString().Trim();
                sb.Clear();

                if (eventDetails.Length >= SchedulerEvent.Name.Length && eventDetails.Substring(0, SchedulerEvent.Name.Length) == SchedulerEvent.Name)
                {
                    eventKind = EventKind.Scheduler;
                }

                // Now that we know the header of the trace, we can decide whether or not to skip it given our pattern
                if (regex != null && !regex.IsMatch(eventName))
                {
                    while (true)
                    {
                        source.MoveNext();
                        if (this.IsEndOfSample(source, source.Current, source.Peek(1)))
                        {
                            break;
                        }
                    }

                    yield return null;
                }
                else
                {
                    LinuxEvent linuxEvent;

                    Frame threadTimeFrame = null;

                    // For the sake of immutability, I have to do a similar if-statement twice. I'm trying to figure out a better way
                    //   but for now this will do.
                    ScheduleSwitch schedSwitch = null;
                    if (eventKind == EventKind.Scheduler)
                    {
                        source.RestoreToMark(markedPosition);
                        schedSwitch = this.ReadScheduleSwitch(source);
                        source.SkipUpTo('\n');
                    }

                    IEnumerable<Frame> frames = this.ReadFramesForSample(comm, pid, tid, threadTimeFrame, source);

                    if (eventKind == EventKind.Scheduler)
                    {
                        linuxEvent = new SchedulerEvent(comm, tid, pid, time, timeProp, cpu, eventName, eventDetails, frames, schedSwitch);
                    }
                    else
                    {
                        linuxEvent = new CpuEvent(comm, tid, pid, time, timeProp, cpu, eventName, eventDetails, frames);
                    }

                    yield return linuxEvent;
                }
            }
        }

        private List<Frame> ReadFramesForSample(string command, int processID, int threadID, Frame threadTimeFrame, FastStream source)
        {
            List<Frame> frames = new List<Frame>();

            if (threadTimeFrame != null)
            {
                frames.Add(threadTimeFrame);
            }

            while (!this.IsEndOfSample(source, source.Current, source.Peek(1)))
            {
                StackFrame stackFrame = this.ReadFrame(source);
                if (this.mapper != null && (stackFrame.Module == "unknown" || stackFrame.Symbol == "unknown"))
                {
                    string[] moduleSymbol = this.mapper.ResolveSymbols(processID, stackFrame.Module, stackFrame);
                    stackFrame = new StackFrame(stackFrame.Address, moduleSymbol[0], moduleSymbol[1]);
                }
                frames.Add(stackFrame);
            }

            frames.Add(new ThreadFrame(threadID, "Thread"));
            frames.Add(new ProcessFrame(command));

            return frames;
        }

        private StackFrame ReadFrame(FastStream source)
        {
            StringBuilder sb = new StringBuilder();

            // Address
            source.SkipWhiteSpace();
            source.ReadAsciiStringUpTo(' ', sb);
            string address = sb.ToString();
            sb.Clear();

            // Trying to get the module and symbol...
            source.SkipWhiteSpace();

            source.ReadAsciiStringUpToLastBeforeTrue('(', sb, delegate (byte c)
            {
                if (c != '\n' && !source.EndOfStream)
                {
                    return true;
                }

                return false;
            });
            string assumedSymbol = sb.ToString();
            sb.Clear();

            source.ReadAsciiStringUpTo('\n', sb);

            string assumedModule = sb.ToString();
            sb.Clear();

            assumedModule = this.RemoveOuterBrackets(assumedModule.Trim());

            string actualModule = assumedModule;
            string actualSymbol = this.RemoveOuterBrackets(assumedSymbol.Trim());

            if (assumedModule.EndsWith(".map"))
            {
                string[] moduleSymbol = this.GetSymbolFromMicrosoftMap(assumedSymbol, assumedModule);
                actualSymbol = string.IsNullOrEmpty(moduleSymbol[1]) ? assumedModule : moduleSymbol[1];
                actualModule = moduleSymbol[0];
            }

            // Can't use Path.GetFileName Because it throws on illegal Windows characters 
            actualModule = GetFileName(actualModule);

            return new StackFrame(address, actualModule, actualSymbol);
        }

        private ScheduleSwitch ReadScheduleSwitch(FastStream source)
        {
            StringBuilder sb = new StringBuilder();

            source.SkipUpTo('=');
            source.MoveNext();

            source.ReadAsciiStringUpTo(' ', sb);
            string prevComm = sb.ToString();
            sb.Clear();

            source.SkipUpTo('=');
            source.MoveNext();

            int prevTid = source.ReadInt();

            source.SkipUpTo('=');
            source.MoveNext();

            int prevPrio = source.ReadInt();

            source.SkipUpTo('=');
            source.MoveNext();

            char prevState = (char)source.Current;

            source.MoveNext();
            source.SkipUpTo('n'); // this is to bypass the ==>
            source.SkipUpTo('=');
            source.MoveNext();

            source.ReadAsciiStringUpTo(' ', sb);
            string nextComm = sb.ToString();
            sb.Clear();

            source.SkipUpTo('=');
            source.MoveNext();

            int nextTid = source.ReadInt();

            source.SkipUpTo('=');
            source.MoveNext();

            int nextPrio = source.ReadInt();

            return new ScheduleSwitch(prevComm, prevTid, prevPrio, prevState, nextComm, nextTid, nextPrio);
        }

        private string RemoveOuterBrackets(string s)
        {
            if (s.Length < 1)
            {
                return s;
            }
            while ((s[0] == '(' && s[s.Length - 1] == ')')
                || (s[0] == '[' && s[s.Length - 1] == ']'))
            {
                s = s.Substring(1, s.Length - 2);
            }

            return s;
        }

        private bool IsNumberChar(char c)
        {
            switch (c)
            {
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                case '0':
                    return true;
            }

            return false;
        }

        private LinuxPerfScriptMapper mapper;
        #endregion
    }

    #region Mapper
    public class LinuxPerfScriptMapper
    {
        public static readonly Regex MapFilePatterns = new Regex(@"^perf\-[0-9]+\.map|.+\.ni\.\{.+\}\.map$");
        public static readonly Regex PerfInfoPattern = new Regex(@"^perfinfo\-[0-9]+\.map$");

        public LinuxPerfScriptMapper(ZipArchive archive, LinuxPerfScriptEventParser parser)
        {
            this.fileSymbolMappers = new Dictionary<string, Mapper>();
            this.processDllGuids = new Dictionary<string, Dictionary<string, string>>();
            this.parser = parser;

            if (archive != null)
            {
                this.PopulateSymbolMapperAndGuids(archive);
            }
        }

        public string[] ResolveSymbols(int processID, string modulePath, StackFrame stackFrame)
        {
            Dictionary<string, string> guids;

            if (this.processDllGuids.TryGetValue(
                string.Format("perfinfo-{0}.map", processID.ToString()), out guids))
            {
                string dllName = modulePath;

                string guid;
                if (guids.TryGetValue(dllName, out guid))
                {
                    string mapName = Path.ChangeExtension(dllName, guid);

                    Mapper mapper;
                    if (this.fileSymbolMappers.TryGetValue(mapName, out mapper))
                    {
                        string symbol;
                        ulong address;
                        if (mapper.TryFindSymbol(ulong.Parse(stackFrame.Address, System.Globalization.NumberStyles.HexNumber),
                            out symbol, out address))
                        {
                            return this.parser.GetSymbolFromMicrosoftMap(symbol);
                        }
                    }
                }

            }

            return new string[] { stackFrame.Module, stackFrame.Symbol };
        }

        #region private
        private void PopulateSymbolMapperAndGuids(ZipArchive archive)
        {
            Contract.Requires(archive != null, nameof(archive));

            foreach (var entry in archive.Entries)
            {
                if (MapFilePatterns.IsMatch(entry.FullName))
                {
                    Mapper mapper = new Mapper();
                    this.fileSymbolMappers[LinuxPerfScriptEventParser.GetFileNameWithoutExtension(entry.FullName)] = mapper;
                    using (Stream stream = entry.Open())
                    {
                        this.parser.ParseSymbolFile(stream, mapper);
                    }
                    mapper.DoneMapping();
                }
                else if (PerfInfoPattern.IsMatch(LinuxPerfScriptEventParser.GetFileName(entry.FullName)))
                {
                    Dictionary<string, string> guids = new Dictionary<string, string>();
                    this.processDllGuids[LinuxPerfScriptEventParser.GetFileName(entry.FullName)] = guids;
                    using (Stream stream = entry.Open())
                    {
                        this.parser.ParsePerfInfoFile(stream, guids);
                    }
                }
            }
        }

        private readonly Dictionary<string, Mapper> fileSymbolMappers;
        private readonly Dictionary<string, Dictionary<string, string>> processDllGuids;
        private readonly LinuxPerfScriptEventParser parser;
        #endregion
    }

    public class Mapper
    {
        public Mapper()
        {
            this.maps = new List<Map>();
        }

        public void DoneMapping()
        {
            // Sort by the start part of the interval... This is for O(log(n)) search time.
            this.maps.Sort((Map x, Map y) => x.Interval.Start.CompareTo(y.Interval.Start));
        }

        public void Add(ulong start, ulong size, string symbol)
        {
            this.maps.Add(new Map(new Interval(start, size), symbol));
        }

        public bool TryFindSymbol(ulong location, out string symbol, out ulong startLocation)
        {
            symbol = "";
            startLocation = 0;

            int start = 0;
            int end = this.maps.Count;
            int mid = (end - start) / 2;

            while (true)
            {
                int index = start + mid;
                if (this.maps[index].Interval.IsWithin(location))
                {
                    symbol = this.maps[index].MapTo;
                    startLocation = this.maps[index].Interval.Start;
                    return true;
                }
                else if (location < this.maps[index].Interval.Start)
                {
                    end = index;
                }
                else if (location >= this.maps[index].Interval.End)
                {
                    start = index;
                }

                if (mid < 1)
                {
                    break;
                }

                mid = (end - start) / 2;
            }

            return false;
        }

        private List<Map> maps;
    }

    internal struct Map
    {
        public Interval Interval { get; }
        public string MapTo { get; }

        public Map(Interval interval, string mapTo)
        {
            this.Interval = interval;
            this.MapTo = mapTo;
        }
    }

    internal class Interval
    {
        public ulong Start { get; }
        public ulong Length { get; }
        public ulong End { get { return this.Start + this.Length; } }

        // Taking advantage of unsigned arithmetic wrap-around to get it done in just one comparison.
        public bool IsWithin(ulong thing)
        {
            return (thing - this.Start) < this.Length;
        }

        public bool IsWithin(ulong thing, bool inclusiveStart, bool inclusiveEnd)
        {
            bool startEqual = inclusiveStart && thing.CompareTo(this.Start) == 0;
            bool endEqual = inclusiveEnd && thing.CompareTo(this.End) == 0;
            bool within = thing.CompareTo(this.Start) > 0 && thing.CompareTo(this.End) < 0;

            return within || startEqual || endEqual;
        }

        public Interval(ulong start, ulong length)
        {
            this.Start = start;
            this.Length = length;
        }

    }
    #endregion

    /// <summary>
    /// Defines the kind of an event for easy casting.
    /// </summary>
    public enum EventKind
    {
        /// <summary>
        /// Represents an event that uses the cpu, and does not do anything special
        /// </summary>
        Cpu,

        /// <summary>
        /// Represents an event that may context switch
        /// </summary>
        Scheduler,
    }

    /// <summary>
    /// A sample that has extra properties to hold scheduled events.
    /// </summary>
    public class SchedulerEvent : LinuxEvent
    {
        public static readonly string Name = "sched_switch";

        /// <summary>
        /// The details of the context switch.
        /// </summary>
        public ScheduleSwitch Switch { get; }

        public SchedulerEvent(
            string comm, int tid, int pid,
            double time, int timeProp, int cpu,
            string eventName, string eventProp, IEnumerable<Frame> callerStacks, ScheduleSwitch schedSwitch) :
            base(EventKind.Scheduler, comm, tid, pid, time, timeProp, cpu, eventName, eventProp, callerStacks)
        {
            this.Switch = schedSwitch;
        }
    }

    /// <summary>
    /// Stores all relevant information retrieved by a context switch stack frame
    /// </summary>
    public class ScheduleSwitch
    {
        public string PreviousCommand { get; }
        public int PreviousPriority { get; }
        public char PreviousState { get; }
        public string NextCommand { get; }
        public int NextThreadID { get; }
        public int NextPriority { get; }
        public int PreviousThreadID { get; }

        public ScheduleSwitch(string prevComm, int prevTid, int prevPrio, char prevState, string nextComm, int nextTid, int nextPrio)
        {
            this.PreviousCommand = prevComm;
            this.PreviousThreadID = prevTid;
            this.PreviousPriority = prevPrio;
            this.PreviousState = prevState;
            this.NextCommand = nextComm;
            this.NextThreadID = nextTid;
            this.NextPriority = nextPrio;
        }
    }

    public class CpuEvent : LinuxEvent
    {
        public CpuEvent(
            string comm, int tid, int pid,
            double time, int timeProp, int cpu,
            string eventName, string eventProp, IEnumerable<Frame> callerStacks) :
            base(EventKind.Cpu, comm, tid, pid, time, timeProp, cpu, eventName, eventProp, callerStacks)
        { }
    }

    /// <summary>
    /// A generic Linux event, all Linux events contain these properties.
    /// </summary>
    public abstract class LinuxEvent
    {
        public EventKind Kind { get; }
        public string Command { get; }
        public int ThreadID { get; }
        public int ProcessID { get; }
        public int CpuNumber { get; }
        public double TimeMSec { get; }
        public int TimeProperty { get; }
        public string EventName { get; }
        public string EventProperty { get; }
        public IEnumerable<Frame> CallerStacks { get; }

        public double Period { get; set; }

        public LinuxEvent(EventKind kind,
            string comm, int tid, int pid,
            double time, int timeProp, int cpu,
            string eventName, string eventProp, IEnumerable<Frame> callerStacks)
        {
            this.Kind = kind;
            this.Command = comm;
            this.ThreadID = tid;
            this.ProcessID = pid;
            this.TimeMSec = time;
            this.TimeProperty = timeProp;
            this.CpuNumber = cpu;
            this.EventName = eventName;
            this.EventProperty = eventProp;
            this.CallerStacks = callerStacks;
        }
    }

    public enum FrameKind
    {
        /// <summary>
        /// An actual stack frame from the simpling data
        /// </summary>
        StackFrame,

        /// <summary>
        /// A stack frame that represents the process of the sample
        /// </summary>
        ProcessFrame,

        /// <summary>
        /// A stack frame that represents the thread of the sample
        /// </summary>
        ThreadFrame,

        /// <summary>
        /// A stack frame that represents either blocked time or cpu time
        /// </summary>
        BlockedCPUFrame
    }

    /// <summary>
    /// A way to define different types of frames with different names on PerfView.
    /// </summary>
    public interface Frame
    {
        FrameKind Kind { get; }
        string DisplayName { get; }
    }

    /// <summary>
    /// Defines a single stack frame on a linux sample.
    /// </summary>
    public struct StackFrame : Frame
    {
        public FrameKind Kind { get { return FrameKind.StackFrame; } }
        public string DisplayName { get { return string.Format("{0}!{1}", this.Module, this.Symbol); } }
        public string Address { get; }
        public string Module { get; }
        public string Symbol { get; }

        public StackFrame(string address, string module, string symbol)
        {
            this.Address = address;
            this.Module = module;
            this.Symbol = symbol;
        }
    }

    /// <summary>
    /// Represents the name of the process.
    /// </summary>
    public struct ProcessFrame : Frame
    {
        public FrameKind Kind { get { return FrameKind.ProcessFrame; } }
        public string DisplayName { get { return this.Name; } }
        public string Name { get; }

        public ProcessFrame(string name)
        {
            this.Name = name;
        }
    }

    /// <summary>
    /// Represents the name of the thread and its ID.
    /// </summary>
    public struct ThreadFrame : Frame
    {
        public FrameKind Kind { get { return FrameKind.ThreadFrame; } }
        public string DisplayName { get { return string.Format("{0} ({1})", this.Name, this.ID); } }
        public string Name { get; }
        public int ID { get; }

        public ThreadFrame(int id, string name)
        {
            this.Name = name;
            this.ID = id;
        }
    }

    /// <summary>
    /// A visual frame that represents whether or not a call stack was blocked or not.
    /// </summary>
    public struct BlockedCPUFrame : Frame
    {
        /// <summary>
        /// Represents whether the stack frame is BLOCKED_TIME or CPU_TIME
        /// </summary>
        public string SubKind { get; }
        public FrameKind Kind { get { return FrameKind.BlockedCPUFrame; } }
        public string DisplayName { get { return this.SubKind; } }

        public int ID { get; }

        public BlockedCPUFrame(int id, string kind)
        {
            this.ID = id;
            this.SubKind = kind;
        }
    }
}

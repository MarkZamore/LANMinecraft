#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Fast, read-only prefilter. PowerShell classifies only the owned rows; the raw CSV remains untouched.
public static class ProcmonCsvScope
{
    public sealed class RootIdentity
    {
        public int ProcessId { get; set; }
        public int ParentProcessId { get; set; }
        public long LocalStartTimeTicks { get; set; }
        public string ProcessName { get; set; } = string.Empty;
    }
    public sealed class Row
    {
        public string Time { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string Pid { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public int RootProcessId { get; set; }
    }
    public sealed class ScopeResult
    {
        public long TotalRows { get; set; }
        public string FirstEventTime { get; set; } = string.Empty;
        public string LastEventTime { get; set; } = string.Empty;
        public List<Row> Rows { get; } = new List<Row>();
        public int[] MissingRootProcessIds { get; set; } = Array.Empty<int>();
    }
    private static readonly Regex ParentPid = new Regex(@"(?:^|, )Parent PID: ([0-9]+)", RegexOptions.Compiled);
    private static readonly Regex ChildPid = new Regex(@"(?:^|, )PID: ([0-9]+)", RegexOptions.Compiled);
    private sealed class CreatedChild {public int ParentId;public int RootId;}

    public static ScopeResult Read(string path, int[] processIds, bool excludeDescendants, int[]? preexistingProcessIds = null, RootIdentity[]? rootIdentities = null)
    {
        var result = new ScopeResult();
        var seeds = new HashSet<int>(processIds);
        var active = new HashSet<int>();
        var rootGroups = new Dictionary<int,int>();
        var pendingChildren = new Dictionary<int,CreatedChild>();
        var identities = new Dictionary<int,RootIdentity>();
        foreach (RootIdentity identity in rootIdentities ?? Array.Empty<RootIdentity>()) identities[identity.ProcessId]=identity;
        foreach (int processId in preexistingProcessIds ?? Array.Empty<int>())
        {
            active.Add(processId);
            rootGroups[processId]=processId;
            seeds.Remove(processId);
        }
        using (var reader = new StreamReader(path, Encoding.UTF8, true, 1024 * 1024))
        {
            string[]? header = ReadRecord(reader);
            if (header == null) throw new InvalidDataException("Empty Process Monitor CSV.");
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < header.Length; index++) columns[header[index]] = index;
            string[] required = { "Time of Day", "Process Name", "PID", "Operation", "Path", "Result", "Detail" };
            foreach (string column in required)
                if (!columns.ContainsKey(column)) throw new InvalidDataException("Missing Process Monitor column: " + column);
            string[]? fields;
            while ((fields = ReadRecord(reader)) != null)
            {
                result.TotalRows++;
                if (fields.Length != header.Length) throw new InvalidDataException("Malformed CSV row " + result.TotalRows);
                if (result.TotalRows == 1) result.FirstEventTime=fields[columns["Time of Day"]];
                result.LastEventTime=fields[columns["Time of Day"]];
                int pid;
                if (!int.TryParse(fields[columns["PID"]], out pid)) continue;
                string operation = fields[columns["Operation"]];
                string detail = fields[columns["Detail"]];
                if (operation == "Process Start")
                {
                    Match parent = ParentPid.Match(detail);
                    int parentId = parent.Success ? int.Parse(parent.Groups[1].Value) : 0;
                    bool rootMatches = seeds.Contains(pid);
                    RootIdentity? identity;
                    if(rootMatches && identities.TryGetValue(pid,out identity))
                        rootMatches=Matches(identity,parentId,fields[columns["Process Name"]],fields[columns["Time of Day"]]);
                    CreatedChild? created;
                    bool knownCreatedChild=pendingChildren.TryGetValue(pid,out created) && created.ParentId==parentId;
                    bool descendant = !excludeDescendants && active.Contains(parentId);
                    active.Remove(pid);
                    if(rootMatches || descendant || knownCreatedChild)
                    {
                        active.Add(pid);
                        rootGroups[pid]=rootMatches ? pid : knownCreatedChild ? created!.RootId : rootGroups[parentId];
                        if(rootMatches) seeds.Remove(pid);
                    }
                    else rootGroups.Remove(pid);
                    pendingChildren.Remove(pid);
                }
                if (!active.Contains(pid)) continue;
                if (!excludeDescendants && operation == "Process Create")
                {
                    Match child = ChildPid.Match(detail);
                    if (child.Success && fields[columns["Result"]]=="SUCCESS")
                    {
                        int childId=int.Parse(child.Groups[1].Value);
                        if(childId>0)
                        {
                            active.Add(childId);rootGroups[childId]=rootGroups[pid];
                            pendingChildren[childId]=new CreatedChild {ParentId=pid,RootId=rootGroups[pid]};
                        }
                    }
                }
                result.Rows.Add(new Row {
                    Time=fields[columns["Time of Day"]], ProcessName=fields[columns["Process Name"]],
                    Pid=fields[columns["PID"]], Operation=operation, Path=fields[columns["Path"]],
                    Result=fields[columns["Result"]], Detail=detail,RootProcessId=rootGroups[pid]
                });
                if (operation == "Process Exit") {active.Remove(pid);rootGroups.Remove(pid);pendingChildren.Remove(pid);}
            }
        }
        result.MissingRootProcessIds=new int[seeds.Count];seeds.CopyTo(result.MissingRootProcessIds);
        return result;
    }

    private static bool Matches(RootIdentity identity,int parentId,string processName,string localTime)
    {
        if(identity.ParentProcessId>0 && identity.ParentProcessId!=parentId) return false;
        if(!string.IsNullOrEmpty(identity.ProcessName) && !string.Equals(
            Path.GetFileNameWithoutExtension(identity.ProcessName),Path.GetFileNameWithoutExtension(processName),StringComparison.OrdinalIgnoreCase)) return false;
        TimeSpan observed;
        if(!TimeSpan.TryParse(localTime.Replace(',','.'),CultureInfo.InvariantCulture,out observed)) return false;
        if(observed.Ticks<0 || observed.Ticks>=TimeSpan.TicksPerDay) return false;
        long difference=Math.Abs(observed.Ticks-identity.LocalStartTimeTicks);
        difference=Math.Min(difference,TimeSpan.TicksPerDay-difference);
        return difference<=2*TimeSpan.TicksPerSecond;
    }

    private static string[]? ReadRecord(TextReader reader)
    {
        var fields = new List<string>(7);
        var field = new StringBuilder(128);
        bool quoted = false;
        bool haveInput = false;
        while (true)
        {
            int next = reader.Read();
            if (next < 0)
            {
                if (quoted) throw new InvalidDataException("Unterminated CSV quote.");
                if (!haveInput && fields.Count == 0) return null;
                fields.Add(field.ToString());
                return fields.ToArray();
            }
            haveInput = true;
            char ch = (char)next;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"' && field.Length == 0) { quoted = true; continue; }
            if (ch == ',') { fields.Add(field.ToString()); field.Clear(); continue; }
            if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                fields.Add(field.ToString());
                return fields.ToArray();
            }
            field.Append(ch);
        }
    }
}

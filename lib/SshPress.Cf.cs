using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SshPress
{
    // A Windows "cloud files" sync provider backed by ssh. The remote folder appears as a normal
    // folder in Explorer; files download when opened; new/changed/renamed/deleted local items are
    // pushed to the server. Uses only ssh.exe and cldapi.dll (both ship with Windows 10/11).
    public static class Cf
    {
        // ------------------------------------------------------------------ native layout (x64)
        [StructLayout(LayoutKind.Sequential)]
        struct SyncReg
        {
            public uint StructSize;
            public IntPtr ProviderName;
            public IntPtr ProviderVersion;
            public IntPtr SyncRootIdentity;
            public uint SyncRootIdentityLength;
            public IntPtr FileIdentity;
            public uint FileIdentityLength;
            public Guid ProviderId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SyncPolicies
        {
            public uint StructSize;
            public ushort HydPrimary, HydModifier;
            public ushort PopPrimary, PopModifier;
            public uint InSync;
            public uint HardLink;
            public uint PlaceholderMgmt;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CbReg { public uint Type; public IntPtr Fn; }

        [StructLayout(LayoutKind.Sequential)]
        struct CallbackInfo
        {
            public uint StructSize;
            public long ConnectionKey;
            public IntPtr CallbackContext;
            public IntPtr VolumeGuidName;
            public IntPtr VolumeDosName;
            public uint VolumeSerialNumber;
            public long SyncRootFileId;
            public IntPtr SyncRootIdentity;
            public uint SyncRootIdentityLength;
            public long FileId;
            public long FileSize;
            public IntPtr FileIdentity;
            public uint FileIdentityLength;
            public IntPtr NormalizedPath;
            public long TransferKey;
            public byte PriorityHint;
            public IntPtr CorrelationVector;
            public IntPtr ProcessInfo;
            public long RequestKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct OpInfo
        {
            public uint StructSize;
            public uint Type;
            public long ConnectionKey;
            public long TransferKey;
            public IntPtr CorrelationVector;
            public IntPtr SyncStatus;
            public long RequestKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct OpData            // CF_OPERATION_PARAMETERS.TransferData (union starts at offset 8)
        {
            public uint ParamSize;
            public uint Pad;
            public uint Flags;
            public int CompletionStatus;
            public IntPtr Buffer;
            public long Offset;
            public long Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct OpPh              // CF_OPERATION_PARAMETERS.TransferPlaceholders
        {
            public uint ParamSize;
            public uint Pad;
            public uint Flags;
            public int CompletionStatus;
            public long TotalCount;
            public IntPtr Array;
            public uint Count;
            public uint Processed;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BasicInfo
        {
            public long Creation, LastAccess, LastWrite, Change;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PhInfo
        {
            public IntPtr RelativeFileName;
            public BasicInfo Basic;
            public long FileSize;
            public IntPtr FileIdentity;
            public uint FileIdentityLength;
            public uint Flags;
            public int Result;
            public long CreateUsn;
        }

        delegate void CfCallback(IntPtr info, IntPtr parameters);

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        static extern int CfRegisterSyncRoot(string path, ref SyncReg reg, ref SyncPolicies pol, uint flags);
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        static extern int CfUnregisterSyncRoot(string path);
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        static extern int CfConnectSyncRoot(string path, CbReg[] table, IntPtr ctx, uint flags, out long key);
        [DllImport("cldapi.dll")]
        static extern int CfDisconnectSyncRoot(long key);
        [DllImport("cldapi.dll")]
        static extern int CfExecute(ref OpInfo info, ref OpData p);
        [DllImport("cldapi.dll", EntryPoint = "CfExecute")]
        static extern int CfExecutePh(ref OpInfo info, ref OpPh p);
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        static extern int CfCreatePlaceholders(string baseDir, IntPtr array, uint count, uint flags, out uint processed);
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
        static extern int CfOpenFileWithOplock(string path, uint flags, out IntPtr handle);
        [DllImport("cldapi.dll")]
        static extern void CfCloseHandle(IntPtr handle);
        [DllImport("cldapi.dll")]
        static extern int CfConvertToPlaceholder(IntPtr h, IntPtr id, uint idLen, uint flags, IntPtr usn, IntPtr overlapped);
        [DllImport("cldapi.dll")]
        static extern int CfSetInSyncState(IntPtr h, uint state, uint flags, IntPtr usn);

        const uint OP_TRANSFER_DATA = 0, OP_TRANSFER_PLACEHOLDERS = 4;
        const uint CB_FETCH_DATA = 0, CB_FETCH_PLACEHOLDERS = 3, CB_NONE = 0xFFFFFFFF;
        const uint PH_MARK_IN_SYNC = 2;
        const int STATUS_UNSUCCESSFUL = unchecked((int)0xC0000001);

        // ------------------------------------------------------------------ state
        static string Target, Key, Root, RootVol, Base;
        static int Port;
        static volatile bool running;
        static int deleteMode;      // 0 = move to ~/.sshpress-trash on the server, 1 = rm -rf, 2 = never touch the server
        static bool NoRemoteDelete { get { return deleteMode == 2; } }
        static long connKey;
        static bool connected;
        static CfCallback cbFetchData, cbFetchPh;       // keep delegates alive
        static FileSystemWatcher watcher;
        static Thread worker;

        static readonly ConcurrentQueue<string> logQ = new ConcurrentQueue<string>();
        static readonly ConcurrentDictionary<string, long> stamp =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);       // local path -> last synced LastWrite (FILETIME)
        static readonly ConcurrentDictionary<string, DateTime> suppress =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Dictionary<string, Item>> populated =
            new Dictionary<string, Dictionary<string, Item>>(StringComparer.OrdinalIgnoreCase);   // rel dir -> listing

        class Item { public string Name; public bool IsDir; public long Len; public long Ft; }
        class Op { public string Kind; public string A; public string B; public DateTime When; public int Tries; }
        static readonly List<Op> ops = new List<Op>();

        static void Log(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (s ?? ""))
            {
                if (c == '\r') continue;
                if (c == '\n') sb.Append(" | ");
                else sb.Append(c < 32 ? '?' : c);      // server text must never be able to inject terminal escape codes
            }
            logQ.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + sb.ToString());
        }

        public static string[] Drain()
        {
            List<string> l = new List<string>();
            string s;
            while (logQ.TryDequeue(out s)) l.Add(s);
            return l.ToArray();
        }

        static string Hr(int hr) { return "0x" + hr.ToString("X8"); }

        // ------------------------------------------------------------------ ssh helpers
        public static bool ValidTarget(string t)
        {
            return t != null && Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9._@%+:\[\]-]{0,254}$");
        }

        public static void Configure(string target, int port, string key)
        {
            Target = target; Port = port; Key = key;
        }

        static string Sq(string s) { return "'" + (s ?? "").Replace("'", "'\\''") + "'"; }

        static string WinQ(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            StringBuilder sb = new StringBuilder("\"");
            int i = 0;
            while (i < arg.Length)
            {
                int bs = 0;
                while (i < arg.Length && arg[i] == '\\') { bs++; i++; }
                if (i == arg.Length) { sb.Append('\\', bs * 2); break; }
                if (arg[i] == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', bs); sb.Append(arg[i]); }
                i++;
            }
            sb.Append('"');
            return sb.ToString();
        }

        static Process Spawn(string remoteCmd)
        {
            List<string> a = new List<string>();
            a.Add("-T"); a.Add("-p"); a.Add(Port.ToString(CultureInfo.InvariantCulture));
            a.Add("-o"); a.Add("BatchMode=yes");
            a.Add("-o"); a.Add("ConnectTimeout=10");
            a.Add("-o"); a.Add("StrictHostKeyChecking=yes");        // never trust a new host key in the background
            a.Add("-o"); a.Add("ServerAliveInterval=15");
            if (!string.IsNullOrEmpty(Key)) { a.Add("-i"); a.Add(Key); }
            a.Add("--");                                          // the target can never be parsed as an ssh option
            a.Add(Target);
            a.Add("sh -c " + Sq(remoteCmd.Replace("\r", "")));       // the .ps1 may have CRLF line endings; sh must never see \r
            List<string> q = new List<string>();
            foreach (string s in a) q.Add(WinQ(s));
            ProcessStartInfo psi = new ProcessStartInfo("ssh", string.Join(" ", q.ToArray()));
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            Process p = new Process();
            p.StartInfo = psi;
            p.Start();
            return p;
        }

        // Run a short remote command to completion. Stdin is closed; hard timeout.
        static int Run(string cmd, out string so, out string se, int timeoutMs)
        {
            so = ""; se = "";
            Process p;
            try { p = Spawn(cmd); }
            catch (Exception ex) { se = "cannot start ssh: " + ex.Message; return -1; }
            p.StandardInput.Close();
            Task<string> o = p.StandardOutput.ReadToEndAsync();
            Task<string> e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } se = "timed out"; return -1; }
            p.WaitForExit();
            so = o.Result; se = e.Result;
            return p.ExitCode;
        }

        static string ListScript(string path)
        {
            return "p=" + Sq(path) + @"
if [ ! -d ""$p"" ]; then printf 'Not a directory: %s\n' ""$p"" >&2; exit 1; fi
if find / -maxdepth 0 -printf '' >/dev/null 2>&1; then
  find ""$p"" -mindepth 1 -maxdepth 1 -printf '%y\t%Y\t%s\t%T@\t%m\t%f\0'
else
  for f in ""$p""/* ""$p""/.[!.]* ""$p""/..?*; do
    if [ -L ""$f"" ]; then y=l; elif [ -d ""$f"" ]; then y=d; elif [ -e ""$f"" ]; then y=f; else continue; fi
    if [ -d ""$f"" ]; then Y=d; s=0; else Y=f; s=$(wc -c < ""$f"" 2>/dev/null | tr -d ' '); fi
    printf '%s\t%s\t%s\t\t\t%s\0' ""$y"" ""$Y"" ""${s:-0}"" ""${f##*/}""
  done
fi
exit 0";
        }

        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                              "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        static bool ValidName(string n)
        {
            if (n.Length == 0 || n == "." || n == "..") return false;
            if (n.EndsWith(" ") || n.EndsWith(".")) return false;
            foreach (char c in n) if (c < 32 || "\\/:*?\"<>|".IndexOf(c) >= 0) return false;
            string up = n.ToUpperInvariant();
            int dot = up.IndexOf('.');
            string stem = dot < 0 ? up : up.Substring(0, dot);
            return Array.IndexOf(Reserved, stem) < 0;
        }

        static List<Item> List(string dir, out string err)
        {
            string so, se;
            int rc = Run(ListScript(dir), out so, out se, 30000);
            if (rc != 0) { err = se.Trim().Length > 0 ? se.Trim() : "ssh exit " + rc; return null; }
            err = null;
            List<Item> list = new List<Item>();
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long nowFt = DateTime.UtcNow.ToFileTimeUtc();
            int skipped = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rec in so.Split('\0'))
            {
                if (rec.Length == 0) continue;
                if (list.Count >= 100000) { Log("ERROR  " + dir + " has more than 100000 entries; showing the first 100000"); break; }
                string[] f = rec.TrimStart('\n').Split(new char[] { '\t' }, 6);
                if (f.Length < 6) continue;
                Item it = new Item();
                it.Name = f[5];
                if (!ValidName(it.Name) || !seen.Add(it.Name)) { skipped++; continue; }     // also drops names that differ only by case
                it.IsDir = f[0] == "d" || (f[0] == "l" && f[1] == "d");
                long len; if (long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out len)) it.Len = len;
                double secs;
                it.Ft = double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out secs)
                    ? epoch.AddSeconds(secs).ToFileTimeUtc() : nowFt;
                list.Add(it);
            }
            if (skipped > 0) Log("skipped " + skipped + " name(s) in " + dir + " that Windows can't store (or that differ only by case)");
            return list;
        }

        // Stream a remote file in chunks to a sink. Returns null on success, else an error.
        static string Download(string remote, long size, Func<byte[], int, long, string> sink)
        {
            Process p = null;
            try
            {
                p = Spawn("exec cat -- " + Sq(remote));
                p.StandardInput.Close();
                Task<string> err = p.StandardError.ReadToEndAsync();
                Stream s = p.StandardOutput.BaseStream;
                byte[] chunk = new byte[1048576];
                long off = 0;
                while (off < size)
                {
                    int want = (int)Math.Min(chunk.Length, size - off);
                    int got = 0;
                    while (got < want)
                    {
                        int n = s.Read(chunk, got, want - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < want)
                    {
                        p.WaitForExit(2000);
                        err.Wait(2000);
                        string e = err.IsCompleted ? err.Result.Trim() : "";
                        return "remote file ended early (" + (off + got) + " of " + size + " bytes)" + (e.Length > 0 ? ": " + e : "");
                    }
                    string r = sink(chunk, got, off);
                    if (r != null) return r;
                    off += got;
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally
            {
                if (p != null) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }
            }
        }

        // Upload a local file to a remote path (temp file + rename; overwriting keeps remote permissions).
        static string Upload(string local, string remote)
        {
            Process p = null;
            try
            {
                string script = "r=" + Sq(remote) + "; t=\"$r.sshpress-tmp.$$\"\n" +
                    "cat > \"$t\" || { rm -f -- \"$t\"; exit 1; }\n" +
                    "if [ -e \"$r\" ]; then cat -- \"$t\" > \"$r\"; rc=$?; rm -f -- \"$t\"; exit $rc\n" +
                    "else mv -f -- \"$t\" \"$r\"; fi";
                p = Spawn(script);
                Task<string> err = p.StandardError.ReadToEndAsync();
                Task<string> outp = p.StandardOutput.ReadToEndAsync();
                using (FileStream fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] buf = new byte[262144];
                    int n;
                    try
                    {
                        Stream so = p.StandardInput.BaseStream;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0) so.Write(buf, 0, n);
                        so.Flush();
                    }
                    catch (IOException) { }
                }
                try { p.StandardInput.Close(); } catch (Exception) { }
                p.WaitForExit();
                if (p.ExitCode != 0) { string e = err.Result.Trim(); return e.Length > 0 ? e : "ssh exit " + p.ExitCode; }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ------------------------------------------------------------------ path mapping
        static string JoinRemote(string dir, string name)
        {
            return dir == "/" ? "/" + name : dir + "/" + name;
        }

        static string RemoteFromRel(string rel)
        {
            return rel.Length == 0 ? Base : JoinRemote(Base, rel);
        }

        static string LocalFromRel(string rel)
        {
            return rel.Length == 0 ? Root : Path.Combine(Root, rel.Replace('/', '\\'));
        }

        // local full path -> relative path with '/', or null if outside the root or unsafe
        static string RelFromLocal(string local)
        {
            if (local == null || !local.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return null;
            string rel = local.Substring(Root.Length).Trim('\\');
            if (rel.Length == 0) return null;
            foreach (string seg in rel.Split('\\')) if (seg == ".." || seg.Length == 0) return null;
            return rel.Replace('\\', '/');
        }

        static string RemoteFromLocal(string local)
        {
            string rel = RelFromLocal(local);
            return rel == null ? null : JoinRemote(Base, rel);
        }

        // Callback paths arrive volume-relative ("\Users\me\SshPress\host\dir"); map to a rel path.
        static string RelFromCallback(CallbackInfo ci)
        {
            if (ci.NormalizedPath != IntPtr.Zero)
            {
                string n = (Marshal.PtrToStringUni(ci.NormalizedPath) ?? "").Trim('\\');
                if (n.StartsWith(RootVol, StringComparison.OrdinalIgnoreCase))
                    return n.Substring(RootVol.Length).Trim('\\').Replace('\\', '/');
            }
            return null;
        }

        // ------------------------------------------------------------------ placeholders
        static IntPtr BuildArray(List<Item> items, string remoteDir, List<IntPtr> allocs)
        {
            int size = Marshal.SizeOf(typeof(PhInfo));
            IntPtr arr = Marshal.AllocHGlobal(size * Math.Max(1, items.Count));
            allocs.Add(arr);
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                PhInfo ph = new PhInfo();
                ph.RelativeFileName = Marshal.StringToHGlobalUni(it.Name);
                allocs.Add(ph.RelativeFileName);
                string id = JoinRemote(remoteDir, it.Name);
                ph.FileIdentity = Marshal.StringToHGlobalUni(id);
                allocs.Add(ph.FileIdentity);
                ph.FileIdentityLength = (uint)(id.Length * 2);
                ph.Basic.Creation = it.Ft; ph.Basic.LastAccess = it.Ft; ph.Basic.LastWrite = it.Ft; ph.Basic.Change = it.Ft;
                ph.Basic.Attributes = it.IsDir ? 0x10u : 0x80u;
                ph.FileSize = it.IsDir ? 0 : it.Len;
                ph.Flags = PH_MARK_IN_SYNC;
                Marshal.StructureToPtr(ph, new IntPtr(arr.ToInt64() + (long)i * size), false);
            }
            return arr;
        }

        static void FreeAll(List<IntPtr> allocs)
        {
            foreach (IntPtr p in allocs) Marshal.FreeHGlobal(p);
            allocs.Clear();
        }

        static Dictionary<string, Item> ToDict(List<Item> items)
        {
            Dictionary<string, Item> d = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (Item it in items) d[it.Name] = it;
            return d;
        }

        static OpInfo MakeOp(CallbackInfo ci, uint type)
        {
            OpInfo oi = new OpInfo();
            oi.StructSize = (uint)Marshal.SizeOf(typeof(OpInfo));
            oi.Type = type;
            oi.ConnectionKey = ci.ConnectionKey;
            oi.TransferKey = ci.TransferKey;
            oi.RequestKey = ci.RequestKey;
            return oi;
        }

        static void OnFetchPlaceholders(IntPtr a, IntPtr b)
        {
            try { FetchPlaceholdersCore(a, b); } catch (Exception ex) { Log("ERROR  open folder: " + ex.Message); }
        }

        static void OnFetchData(IntPtr a, IntPtr b)
        {
            try { FetchDataCore(a, b); } catch (Exception ex) { Log("ERROR  download: " + ex.Message); }
        }

        // Explorer opened a folder: list it on the server and hand Windows the entries.
        static void FetchPlaceholdersCore(IntPtr infoPtr, IntPtr prm)
        {
            CallbackInfo ci = (CallbackInfo)Marshal.PtrToStructure(infoPtr, typeof(CallbackInfo));
            OpInfo oi = MakeOp(ci, OP_TRANSFER_PLACEHOLDERS);
            OpPh ph = new OpPh();
            ph.ParamSize = (uint)Marshal.SizeOf(typeof(OpPh));
            List<IntPtr> allocs = new List<IntPtr>();
            try
            {
                string rel = RelFromCallback(ci);
                if (rel == null) throw new IOException("cannot map callback path");
                string remoteDir = RemoteFromRel(rel);
                string err;
                List<Item> items = List(remoteDir, out err);
                if (items == null) throw new IOException(err);
                Log("open  " + remoteDir + "  (" + items.Count + " items)");
                string localDir = LocalFromRel(rel);
                foreach (Item it in items) if (!it.IsDir) stamp[Path.Combine(localDir, it.Name)] = it.Ft;
                lock (populated) populated[rel] = ToDict(items);
                ph.Flags = 2;                                   // directory is now fully populated
                ph.CompletionStatus = 0;
                ph.TotalCount = items.Count;
                ph.Array = items.Count > 0 ? BuildArray(items, remoteDir, allocs) : IntPtr.Zero;
                ph.Count = (uint)items.Count;
                int hr = CfExecutePh(ref oi, ref ph);
                if (hr != 0) Log("ERROR  placeholders failed " + Hr(hr));
            }
            catch (Exception ex)
            {
                Log("ERROR  open folder: " + ex.Message);
                ph.Flags = 0; ph.CompletionStatus = STATUS_UNSUCCESSFUL; ph.TotalCount = 0; ph.Array = IntPtr.Zero; ph.Count = 0;
                CfExecutePh(ref oi, ref ph);
            }
            finally { FreeAll(allocs); }
        }

        // A file was opened and isn't on disk yet: stream it from the server into Windows.
        static void FetchDataCore(IntPtr infoPtr, IntPtr prm)
        {
            CallbackInfo ci = (CallbackInfo)Marshal.PtrToStructure(infoPtr, typeof(CallbackInfo));
            OpInfo oi = MakeOp(ci, OP_TRANSFER_DATA);
            long reqOff = Marshal.ReadInt64(prm, 16), reqLen = Marshal.ReadInt64(prm, 24);
            IntPtr buf = Marshal.AllocHGlobal(1048576);
            string error;
            string remote = null;
            try
            {
                string rel = RelFromCallback(ci);
                remote = rel == null ? null : RemoteFromRel(rel);
                if (remote == null) throw new IOException("cannot map callback path");
                Log("download  " + remote + "  (" + ci.FileSize + " bytes)");
                error = Download(remote, ci.FileSize, delegate(byte[] data, int count, long offset)
                {
                    Marshal.Copy(data, 0, buf, count);
                    OpData d = new OpData();
                    d.ParamSize = (uint)Marshal.SizeOf(typeof(OpData));
                    d.Buffer = buf; d.Offset = offset; d.Length = count; d.CompletionStatus = 0;
                    int hr = CfExecute(ref oi, ref d);
                    return hr == 0 ? null : "CfExecute " + Hr(hr);
                });
            }
            catch (Exception ex) { error = ex.Message; }
            if (error != null)
            {
                Log("ERROR  download " + (remote ?? "?") + ": " + error);
                OpData f = new OpData();
                f.ParamSize = (uint)Marshal.SizeOf(typeof(OpData));
                f.CompletionStatus = STATUS_UNSUCCESSFUL; f.Offset = reqOff; f.Length = reqLen;
                CfExecute(ref oi, ref f);
            }
            Marshal.FreeHGlobal(buf);
        }

        // ------------------------------------------------------------------ local -> remote
        static bool IsSuppressed(string path)
        {
            string p = path;
            while (!string.IsNullOrEmpty(p) && p.Length > Root.Length)
            {
                DateTime t;
                if (suppress.TryGetValue(p, out t) && (DateTime.UtcNow - t).TotalSeconds < 20) return true;
                p = Path.GetDirectoryName(p);
            }
            return false;
        }

        static bool IsIgnored(string path)
        {
            string n = Path.GetFileName(path);
            return string.Equals(n, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(n, "Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                   n.IndexOf(".sshpress-tmp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void Enqueue(string kind, string a, string b, int delayMs)
        {
            lock (ops)
            {
                if (kind == "U")
                    foreach (Op o in ops)
                        if (o.Kind == "U" && string.Equals(o.A, a, StringComparison.OrdinalIgnoreCase)) { o.When = DateTime.UtcNow.AddMilliseconds(delayMs); return; }
                Op op = new Op(); op.Kind = kind; op.A = a; op.B = b; op.When = DateTime.UtcNow.AddMilliseconds(delayMs);
                ops.Add(op);
            }
        }

        static void WorkerLoop()
        {
            while (running)
            {
                Op op = null;
                lock (ops)
                {
                    for (int i = 0; i < ops.Count; i++)
                        if (ops[i].When <= DateTime.UtcNow) { op = ops[i]; ops.RemoveAt(i); break; }
                }
                if (op == null) { Thread.Sleep(200); continue; }
                try
                {
                    if (op.Kind == "U") DoUpload(op);
                    else if (op.Kind == "D") DoDelete(op);
                    else if (op.Kind == "R") DoRename(op);
                }
                catch (Exception ex) { Log("ERROR  " + op.Kind + " " + op.A + ": " + ex.Message); }
            }
        }

        static void MarkInSync(string path, string remote, bool isPlaceholder)
        {
            IntPtr h;
            if (CfOpenFileWithOplock(path, 2, out h) != 0) return;
            try
            {
                if (isPlaceholder) CfSetInSyncState(h, 1, 0, IntPtr.Zero);
                else
                {
                    IntPtr id = Marshal.StringToHGlobalUni(remote);
                    try { CfConvertToPlaceholder(h, id, (uint)(remote.Length * 2), 1, IntPtr.Zero, IntPtr.Zero); }
                    finally { Marshal.FreeHGlobal(id); }
                }
            }
            finally { CfCloseHandle(h); }
        }

        static void DoUpload(Op op)
        {
            string path = op.A;
            if (IsSuppressed(path) || IsIgnored(path)) return;
            string remote = RemoteFromLocal(path);
            if (remote == null) return;

            if (Directory.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;      // already a placeholder
                string so, se;
                Log("mkdir  " + remote);
                if (Run("mkdir -p -- " + Sq(remote), out so, out se, 30000) != 0) Log("ERROR  mkdir " + remote + ": " + se.Trim());
                return;
            }
            if (!File.Exists(path)) return;

            int attrs = (int)File.GetAttributes(path);
            if ((attrs & 0x400000) != 0 || (attrs & 0x40000) != 0) return;                      // not downloaded yet: nothing to upload
            bool isPh = (attrs & 0x400) != 0;
            long ft = File.GetLastWriteTimeUtc(path).ToFileTimeUtc();
            long known;
            if (stamp.TryGetValue(path, out known) && known == ft) return;                       // unchanged since last sync

            FileStream probe = null;
            try { probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException)
            {
                if (++op.Tries < 8) Enqueue("U", path, null, 2000);                              // still being written / locked
                return;
            }
            finally { if (probe != null) probe.Dispose(); }

            Log("upload  " + remote);
            string err = Upload(path, remote);
            if (err != null) { Log("ERROR  upload " + remote + ": " + err); return; }
            stamp[path] = ft;
            MarkInSync(path, remote, isPh);
        }

        // Deleting locally moves the item into ~/.sshpress-trash on the server (recoverable) unless asked otherwise.
        static string DeleteScript(string path, int mode)
        {
            if (mode == 1) return "rm -rf -- " + Sq(path);
            return "s=" + Sq(path) + "\n" +
                "[ -e \"$s\" ] || [ -L \"$s\" ] || exit 0\n" +
                "t=\"$HOME/.sshpress-trash\"; umask 077; mkdir -p -- \"$t\" || exit 1\n" +
                "b=${s##*/}; mv -- \"$s\" \"$t/$b.$(date +%s).$$\"";
        }

        // exit 4 = source missing on the server. Otherwise a normal mv: an existing target is replaced, like any SFTP rename.
        static string RenameScript(string from, string to)
        {
            return "a=" + Sq(from) + "; b=" + Sq(to) + "\n" +
                "if [ ! -e \"$a\" ] && [ ! -L \"$a\" ]; then exit 4; fi\n" +
                "mv -- \"$a\" \"$b\"";
        }

        static void DoDelete(Op op)
        {
            string path = op.A;
            if (NoRemoteDelete || IsSuppressed(path) || IsIgnored(path)) return;
            string remote = RemoteFromLocal(path);
            if (remote == null) return;
            if (File.Exists(path) || Directory.Exists(path)) return;                             // re-created (atomic save): keep remote
            long old; stamp.TryRemove(path, out old);
            string so, se;
            Log(deleteMode == 1 ? "delete  " + remote : "delete  " + remote + "  (moved to ~/.sshpress-trash)");
            if (Run(DeleteScript(remote, deleteMode), out so, out se, 60000) != 0) Log("ERROR  delete " + remote + ": " + se.Trim());
        }

        static void DoRename(Op op)
        {
            string from = op.A, to = op.B;
            if (IsSuppressed(to) || IsIgnored(to)) return;
            string rFrom = RemoteFromLocal(from), rTo = RemoteFromLocal(to);
            if (rTo == null) { Enqueue("D", from, null, 0); return; }
            if (rFrom == null || NoRemoteDelete) { Enqueue("U", to, null, 500); return; }
            string so, se;
            Log("rename  " + rFrom + "  ->  " + rTo);
            int rc = Run(RenameScript(rFrom, rTo), out so, out se, 60000);
            if (rc == 4) { Enqueue("U", to, null, 300); return; }        // never existed remotely (temp file saved over a real one): upload it
            if (rc != 0) { Log("ERROR  rename " + rFrom + ": " + se.Trim()); return; }
            long v;
            if (stamp.TryRemove(from, out v)) stamp[to] = v;
            string prefix = from + "\\";
            foreach (string k in new List<string>(stamp.Keys))
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && stamp.TryRemove(k, out v)) stamp[to + "\\" + k.Substring(prefix.Length)] = v;
            lock (populated)
            {
                string relFrom = RelFromLocal(from), relTo = RelFromLocal(to);
                if (relFrom != null && relTo != null)
                    foreach (string k in new List<string>(populated.Keys))
                        if (string.Equals(k, relFrom, StringComparison.OrdinalIgnoreCase) || k.StartsWith(relFrom + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            Dictionary<string, Item> d = populated[k]; populated.Remove(k);
                            populated[relTo + k.Substring(relFrom.Length)] = d;
                        }
            }
        }

        static void StartWatcher()
        {
            watcher = new FileSystemWatcher(Root);
            watcher.IncludeSubdirectories = true;
            watcher.InternalBufferSize = 65536;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
            watcher.Created += delegate(object s, FileSystemEventArgs e) { Enqueue("U", e.FullPath, null, 1200); };
            watcher.Changed += delegate(object s, FileSystemEventArgs e)
            {
                if (!Directory.Exists(e.FullPath)) Enqueue("U", e.FullPath, null, 1200);
            };
            watcher.Deleted += delegate(object s, FileSystemEventArgs e) { Enqueue("D", e.FullPath, null, 1000); };
            watcher.Renamed += delegate(object s, RenamedEventArgs e) { Enqueue("R", e.OldFullPath, e.FullPath, 400); };
            watcher.Error += delegate(object s, ErrorEventArgs e) { Log("ERROR  file watcher: " + e.GetException().Message + " (press R to resync)"); };
            watcher.EnableRaisingEvents = true;
        }

        // ------------------------------------------------------------------ refresh from server
        public static void Refresh()
        {
            List<string> dirs;
            lock (populated) dirs = new List<string>(populated.Keys);
            int added = 0, removed = 0;
            foreach (string rel in dirs)
            {
                try
                {
                    string localDir = LocalFromRel(rel);
                    if (!Directory.Exists(localDir)) { lock (populated) populated.Remove(rel); continue; }
                    string err;
                    List<Item> now = List(RemoteFromRel(rel), out err);
                    if (now == null) { Log("ERROR  refresh " + rel + ": " + err); continue; }
                    Dictionary<string, Item> nd = ToDict(now), od;
                    lock (populated) { if (!populated.TryGetValue(rel, out od)) continue; }

                    foreach (KeyValuePair<string, Item> kv in od)
                    {
                        Item n; string local = Path.Combine(localDir, kv.Key);
                        bool gone = !nd.TryGetValue(kv.Key, out n);
                        bool changed = !gone && (n.IsDir != kv.Value.IsDir || (!n.IsDir && (n.Len != kv.Value.Len || n.Ft != kv.Value.Ft)));
                        if (!gone && !changed) continue;
                        if (!kv.Value.IsDir)
                        {
                            long k;
                            if (File.Exists(local) && (!stamp.TryGetValue(local, out k) || k != File.GetLastWriteTimeUtc(local).ToFileTimeUtc()))
                            { Log("kept  " + local + "  (changed locally; not replaced)"); continue; }
                        }
                        suppress[local] = DateTime.UtcNow;
                        try
                        {
                            if (Directory.Exists(local)) Directory.Delete(local, true); else if (File.Exists(local)) File.Delete(local);
                            long v; stamp.TryRemove(local, out v);
                            if (gone) removed++;
                        }
                        catch (Exception ex) { Log("ERROR  could not remove " + local + ": " + ex.Message); }
                    }

                    List<Item> create = new List<Item>();
                    foreach (Item it in now)
                    {
                        string local = Path.Combine(localDir, it.Name);
                        if (!File.Exists(local) && !Directory.Exists(local)) create.Add(it);
                    }
                    if (create.Count > 0)
                    {
                        List<IntPtr> allocs = new List<IntPtr>();
                        try
                        {
                            foreach (Item it in create) suppress[Path.Combine(localDir, it.Name)] = DateTime.UtcNow;
                            IntPtr arr = BuildArray(create, RemoteFromRel(rel), allocs);
                            uint done;
                            int hr = CfCreatePlaceholders(localDir, arr, (uint)create.Count, 0, out done);
                            if (hr != 0) Log("ERROR  create placeholders " + Hr(hr));
                            foreach (Item it in create) if (!it.IsDir) stamp[Path.Combine(localDir, it.Name)] = it.Ft;
                            added += (int)done;
                        }
                        finally { FreeAll(allocs); }
                    }
                    lock (populated) populated[rel] = nd;
                }
                catch (Exception ex) { Log("ERROR  refresh " + rel + ": " + ex.Message); }
            }
            Log("refreshed  (" + added + " added, " + removed + " removed)");
        }

        // ------------------------------------------------------------------ lifecycle
        public static int Unregister(string root)
        {
            return CfUnregisterSyncRoot(Path.GetFullPath(root).TrimEnd('\\'));
        }

        // Returns null on success, otherwise an error message.
        public static string Start(string target, int port, string key, string root, string remotePath, int remoteDeleteMode)
        {
            try
            {
                if (!Environment.Is64BitProcess) return "Run this in 64-bit PowerShell.";
                if (!ValidTarget(target)) return "Invalid target '" + target + "'. Use user@host or a Host alias from ~/.ssh/config.";
                if (port < 1 || port > 65535) return "Invalid port.";
                if (!string.IsNullOrEmpty(key) && key.StartsWith("-")) return "Invalid key path.";
                Configure(target, port, key);
                deleteMode = remoteDeleteMode;
                Root = Path.GetFullPath(root).TrimEnd('\\');
                Directory.CreateDirectory(Root);
                RootVol = Root.Substring(Path.GetPathRoot(Root).Length).Trim('\\');

                string so, se;
                string cd = remotePath == "~" ? "cd" : "cd -- " + Sq(remotePath);
                if (Run(cd + " && pwd", out so, out se, 30000) != 0) return "Cannot open remote path '" + remotePath + "': " + se.Trim();
                Base = so.Trim();
                if (Base.Length > 1) Base = Base.TrimEnd('/');

                SyncReg reg = new SyncReg();
                reg.StructSize = (uint)Marshal.SizeOf(typeof(SyncReg));
                reg.ProviderName = Marshal.StringToHGlobalUni("SshPress");
                reg.ProviderVersion = Marshal.StringToHGlobalUni("1.0");
                byte[] id = Encoding.Unicode.GetBytes(target);
                reg.SyncRootIdentity = Marshal.AllocHGlobal(id.Length);
                Marshal.Copy(id, 0, reg.SyncRootIdentity, id.Length);
                reg.SyncRootIdentityLength = (uint)id.Length;
                reg.ProviderId = new Guid("6f0c1b7e-5d2a-4c53-9a43-3a5b2c0d9e11");

                SyncPolicies pol = new SyncPolicies();
                pol.StructSize = (uint)Marshal.SizeOf(typeof(SyncPolicies));
                pol.HydPrimary = 2;      // FULL: a file is downloaded completely when opened
                pol.PopPrimary = 0;      // PARTIAL: folders are filled in when Explorer opens them
                int hr = CfRegisterSyncRoot(Root, ref reg, ref pol, 1 | 4);   // UPDATE | MARK_IN_SYNC_ON_ROOT
                if (hr != 0) return "CfRegisterSyncRoot failed " + Hr(hr) + ". The folder must be on a local NTFS drive and outside OneDrive.";

                cbFetchData = new CfCallback(OnFetchData);
                cbFetchPh = new CfCallback(OnFetchPlaceholders);
                CbReg[] table = new CbReg[3];
                table[0].Type = CB_FETCH_DATA; table[0].Fn = Marshal.GetFunctionPointerForDelegate(cbFetchData);
                table[1].Type = CB_FETCH_PLACEHOLDERS; table[1].Fn = Marshal.GetFunctionPointerForDelegate(cbFetchPh);
                table[2].Type = CB_NONE; table[2].Fn = IntPtr.Zero;
                hr = CfConnectSyncRoot(Root, table, IntPtr.Zero, 0, out connKey);
                if (hr != 0) return "CfConnectSyncRoot failed " + Hr(hr);
                connected = true;

                running = true;
                worker = new Thread(WorkerLoop);
                worker.IsBackground = true;
                worker.Start();
                StartWatcher();
                Log("connected  " + target + ":" + Base + "  ->  " + Root);
                return null;
            }
            catch (Exception ex) { return ex.ToString(); }
        }

        public static void Stop()
        {
            running = false;
            try { if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; } } catch (Exception) { }
            try { if (connected) { CfDisconnectSyncRoot(connKey); connected = false; } } catch (Exception) { }
        }

        public static int Pending() { lock (ops) return ops.Count; }

        // ------------------------------------------------------------------ test hooks (no Windows APIs)
        public static string[] TestList(string dir)
        {
            string err; List<Item> l = List(dir, out err);
            if (l == null) return new string[] { "ERR " + err };
            List<string> r = new List<string>();
            foreach (Item i in l) r.Add((i.IsDir ? "d " : "f ") + i.Len + " " + i.Name);
            return r.ToArray();
        }
        public static string TestUpload(string local, string remote) { return Upload(local, remote); }
        public static string TestDownload(string remote, long size, string localOut)
        {
            using (FileStream fs = new FileStream(localOut, FileMode.Create))
                return Download(remote, size, delegate(byte[] d, int n, long off) { fs.Write(d, 0, n); return null; });
        }
        public static string TestScript(string which, string a, string b)
        {
            string cmd = which == "trash" ? DeleteScript(a, 0) : which == "rm" ? DeleteScript(a, 1) : RenameScript(a, b);
            return TestRun(cmd);
        }
        public static string TestRun(string cmd)
        {
            string so, se; int rc = Run(cmd, out so, out se, 30000);
            return rc + "|" + so.Trim() + "|" + se.Trim();
        }
    }
}

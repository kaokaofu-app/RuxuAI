using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Jiyue
{
    internal sealed class ConversationRecord
    {
        public string Id, Title, Scope, Context, RecognizedText, TranscriptRtf, RetryQuestion;
        public string SourceTitle;
        public DateTime CapturedUtc;
        public bool ImageIsPagePreview;
        public bool SourceExpanded;
        public bool ArticleCapture, AwaitingPartialApproval;
        public string ArticleNotice;
        public int ArticleSection, SectionHistoryStart;
        [ScriptIgnore] public byte[] Image;
        public int PendingStart = -1;
        public List<string[]> History = new List<string[]>();
        public DateTime UpdatedUtc;
        public bool CanRetry;
        internal ConversationRecord TextOnly()
        {
            if(Image==null)return this;
            var copy=(ConversationRecord)MemberwiseClone();
            copy.Image=null;
            return copy;
        }
    }

    internal static class ConversationStore
    {
        // Blob names are random. Even plaintext hashes live only in the encrypted index.
        sealed class Blob { public string Name, Hash; }
        sealed class Entry { public string Id; public Blob Body, Picture; }
        const string Index = "recent.dat", Mirror = "recent-copy.dat";
        internal const int Limit=50, ImageLimit=20;
        static readonly object Gate = new object();
        static readonly HashSet<string> Damaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jiyue", "ReadingHistory");

        public static string LastLoadWarning { get; private set; }
        public static List<ConversationRecord> Load() { return LoadAt(Folder); }
        public static void Save(IList<ConversationRecord> records) { SaveAt(Folder, records); }

        static byte[] Json(object value)
        {
            return Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(value));
        }
        static T Parse<T>(byte[] bytes)
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<T>(Encoding.UTF8.GetString(bytes));
        }
        static string Digest(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
        }
        static byte[] Protect(byte[] bytes) { return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
        static byte[] ReadProtected(string path)
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        }
        static bool IsDamaged(Exception ex)
        {
            return ex is InvalidDataException || ex is CryptographicException || ex is FileNotFoundException ||
                   ex is ArgumentException || ex is InvalidOperationException || ex is FormatException;
        }

        static void AtomicWrite(string path, byte[] bytes)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path), "tmp-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static bool BlobName(string name)
        {
            Guid ignored;
            return name != null && name.Length == 40 && name.StartsWith("obj-", StringComparison.Ordinal) &&
                   name.EndsWith(".bin", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(4,32), "N", out ignored);
        }
        static byte[] ReadBlob(string root, Blob blob)
        {
            if (blob == null || !BlobName(blob.Name)) throw new InvalidDataException("对话文件索引无效。");
            string path = Path.Combine(root, blob.Name);
            try
            {
                byte[] data = ReadProtected(path);
                if (Digest(data) != blob.Hash) throw new InvalidDataException("对话文件内容校验失败。");
                return data;
            }
            catch (Exception ex) { if (IsDamaged(ex)) Damaged.Add(path); throw; }
        }

        static List<Entry> ReadIndex(string root, out bool recovered)
        {
            recovered = false;
            bool found = false;
            foreach (string name in new[]{Index, Mirror})
            {
                string path = Path.Combine(root, name);
                if (!File.Exists(path)) continue;
                found = true;
                try
                {
                    var entries = Parse<List<Entry>>(ReadProtected(path));
                    if (entries == null) throw new InvalidDataException();
                    recovered = name == Mirror;
                    return entries;
                }
                catch (Exception ex) { if (!IsDamaged(ex)) throw; }
            }
            if (found) throw new InvalidDataException("两个阅读历史索引均无法读取。已保留原文件。");
            return new List<Entry>();
        }

        static FileStream LockFolder(string root)
        {
            return new FileStream(Path.Combine(root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        static List<ConversationRecord> LoadAt(string root)
        {
            lock (Gate)
            {
                var result = new List<ConversationRecord>();
                LastLoadWarning = null;
                if (!Directory.Exists(root)) return result;
                using (LockFolder(root))
                {
                    bool recovered;
                    var entries = ReadIndex(root, out recovered);
                    int missing = 0, pictures = 0;
                    foreach (var entry in entries)
                    {
                        if (result.Count == Limit) break;
                        if (entry == null || String.IsNullOrWhiteSpace(entry.Id)) { missing++; continue; }
                        if (result.Any(x => x.Id == entry.Id)) continue;
                        ConversationRecord record;
                        try
                        {
                            record = Parse<ConversationRecord>(ReadBlob(root, entry.Body));
                            if (record == null || record.Id != entry.Id) throw new InvalidDataException();
                        }
                        catch (Exception ex) { if (!IsDamaged(ex)) throw; missing++; continue; }
                        if (entry.Picture != null && result.Count<ImageLimit)
                        {
                            try { record.Image = ReadBlob(root, entry.Picture); }
                            catch (Exception ex) { if (!IsDamaged(ex)) throw; pictures++; }
                        }
                        record.History = (record.History ?? new List<string[]>()).Where(p => p != null && p.Length == 2 &&
                            (p[0] == "user" || p[0] == "assistant") && p[1] != null).ToList();
                        result.Add(record);
                    }
                    var warnings = new List<string>();
                    if (recovered) warnings.Add("已从备份索引恢复阅读历史");
                    if (missing > 0) warnings.Add(missing + " 条对话损坏或缺失");
                    if (pictures > 0) warnings.Add(pictures + " 张原图无法读取，已保留对话文字");
                    if (warnings.Count > 0) LastLoadWarning = String.Join("。", warnings) + "。";
                }
                return result;
            }
        }

        static Blob StoreBlob(string root, byte[] bytes, Dictionary<string,Blob> existing)
        {
            string hash = Digest(bytes);
            Blob blob;
            if (existing.TryGetValue(hash, out blob))
            {
                string path = Path.Combine(root, blob.Name);
                if (File.Exists(path) && !Damaged.Contains(path)) return blob;
            }
            blob = new Blob { Name = "obj-" + Guid.NewGuid().ToString("N") + ".bin", Hash = hash };
            AtomicWrite(Path.Combine(root, blob.Name), Protect(bytes));
            existing[hash] = blob;
            return blob;
        }

        static void SaveAt(string root, IList<ConversationRecord> records)
        {
            if (records == null) throw new ArgumentNullException("records");
            if (records.Any(r => r == null || String.IsNullOrWhiteSpace(r.Id)))
                throw new ArgumentException("每条阅读历史都需要有效的标识。");
            var recent = records.OrderByDescending(r => r.UpdatedUtc).GroupBy(r => r.Id, StringComparer.Ordinal)
                                .Select(g => g.First()).Take(Limit).ToList();
            lock (Gate)
            {
                Directory.CreateDirectory(root);
                using (LockFolder(root))
                {
                    bool recovered;
                    var previous = ReadIndex(root, out recovered);
                    var existing = new Dictionary<string,Blob>(StringComparer.Ordinal);
                    foreach (var entry in previous.Where(e => e != null))
                        foreach (var blob in new[]{entry.Body, entry.Picture})
                            if (blob != null && BlobName(blob.Name) && blob.Hash != null) existing[blob.Hash] = blob;
                    var entries = new List<Entry>();
                    var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in recent)
                    {
                        var body = StoreBlob(root, Json(r), existing);
                        var pic = entries.Count>=ImageLimit || r.Image == null || r.Image.Length == 0 ? null : StoreBlob(root, r.Image, existing);
                        entries.Add(new Entry { Id = r.Id, Body = body, Picture = pic });
                        live.Add(body.Name);
                        if (pic != null) live.Add(pic.Name);
                    }
                    // Commit the immutable objects before publishing their index. Keep both indexes on this generation.
                    var encrypted = Protect(Json(entries));
                    AtomicWrite(Path.Combine(root, Index), encrypted);
                    try
                    {
                        AtomicWrite(Path.Combine(root, Mirror), encrypted);
                        foreach (string path in Directory.GetFiles(root, "obj-*.bin"))
                        {
                            string name = Path.GetFileName(path);
                            if (BlobName(name) && !live.Contains(name)) { File.Delete(path); Damaged.Remove(path); }
                        }
                    }
                    catch (IOException ex) { throw new IOException("阅读历史已保存，更新备份或清理旧文件时失败，可以重试。", ex); }
                    catch (UnauthorizedAccessException ex) { throw new IOException("阅读历史已保存，但无法更新备份或清理旧文件，请检查目录权限。", ex); }
                }
            }
        }
    }
}

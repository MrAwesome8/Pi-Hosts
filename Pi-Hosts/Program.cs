using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using static Pi_Hosts.StringHelper;

namespace Pi_Hosts {

    public enum ListType {
        Good,
        Cached,
        BackedUp,
        Missing
    }

    internal static class Program {
        private const string BackupListPath = "backup.list";
        private const string BlockListPath = "block.list";
        private const string DeadListPath = "dead.list";
        private const string Hole = "0.0.0.0";
        private const int MaxFileSizeMb = 80;
        static int domainCount = 0;

        //github maxfile upload is 100MB. (rec.50MB)
        private const int MIB = 1049000;

        private const string StatusPath = "status.txt";

        private static ISet<string> BackupList;
        private static ISet<string> BlockLists;
        private static ISet<string> GoodList = new SortedSet<string>();
        private static ISet<string> MissingList;
        private static Encoding[] retries = new[] { Encoding.UTF8, Encoding.Unicode, Encoding.ASCII, Encoding.UTF32, Encoding.UTF7, Encoding.BigEndianUnicode };
        private static int retryCount = 0;
        private static bool shouldUpdate = true;

        private const string SkipListPath = "skip.list";
        private static ISet<string> SkipList = new SortedSet<string>();

        private static void ArchiveLists(TreeNode<string> root) {
            List<TreeNode<string>> leafs = new List<TreeNode<string>>();
            root.ForEachNode(node => {
                if (!node.HasChildren) {
                    leafs.Add(node);
                }
            });

            leafs./*AsParallel().ForAll*/ForEach(node => {
                string path = BuildPath(node);

                var url = GetUrlFromNodePath(path);

                if (path.Contains('?')) {
                    path = path.Substring(0, path.IndexOf('?')); //remove query from path
                }

                try {
                    FileInfo file = new FileInfo(path);
                    if (file.Attributes == FileAttributes.Directory) {
                        file = new FileInfo(Path.Combine(path, "unnamed.txt"));
                    }
                    path = file.FullName;

                    bool valid = url.IsValid();
                    if (file.Exists && file.Length > 0) {
                        if (!valid) { MarkListAs(url, ListType.BackedUp); return; }

                        var stillRecent = false;// File.GetLastWriteTimeUtc(path).AddMinutes(30) > DateTime.UtcNow;

                        if(stillRecent) {
                            Console.WriteLine(path + " still recent");
                        }

                        if (!shouldUpdate || stillRecent) { MarkListAs(url, ListType.Cached); return; }

                        var result = WriteListToFile(path, url);
                        if (result == ListType.Good) {
                            MarkListAs(url, ListType.Good);
                        } else {
                            MarkListAs(url, ListType.BackedUp);
                        }

                        domainCount += File.ReadLines(path).Count();
                    } else {
                        if (!valid) { MarkListAs(url, ListType.Missing); return; } //url doesnt exist and we have no backup :(

                        //create path
                        file.Directory.Create();
                        using (File.Create(path)) { }

                        //download content
                        var result = WriteListToFile(path, url);
                        MarkListAs(url, result);
                    }
                } catch (Exception) {
                    MarkListAs(url, ListType.Missing);
                }
            });
        }

        private static string BuildPath(TreeNode<string> node) {
            string path = "";
            var curNode = node;
            while (curNode != null) {
                path = Path.Combine(curNode.Item, path);
                curNode = curNode.Parent;
            }
            return path;
        }

        private static TreeNode<string> BuildTree(ISet<string> fromList, string root) {
            TreeNode<string> blockTree = new TreeNode<string>(root);

            fromList.AsParallel().ForAll(list => {
                if (list.StartsWith('#') || !list.Exists()) return; //skip
                var url = new Uri(list);

                var segments = url.Segments.Where(x => !x.Equals("/") && x.Exists()).ToArray();

                if (url.Query.Exists() && segments.Count() > 0) {
                    segments[segments.Length - 1] += url.Query;
                }

                TreeNode<string> curNode;

                lock (blockTree) {
                    curNode = blockTree.AddChild(url.Scheme);
                }

                curNode = curNode.AddChild(url.Host);

                foreach (var node in segments) {
                    curNode = curNode.AddChild(node.Replace("/", ""));
                }
            });

            return blockTree;
        }

        private static double BytesToMebibytes(long bytes) => bytes / (double)MIB;

        private static IEnumerable<string> DownloadBlockList(string url, Encoding encoding) {
            string results = $"Attempting to download: {url}...";

            //if (!url.IsValid()) {
            //    Console.WriteLine(results + "Failed");
            //    return null;
            //}

            if (url.Contains("github.com") && !url.Contains("/raw")) {
                url = GetRawGithub(url);
            }

            if (url.Contains("pastebin.com") && !url.Contains("/raw")) {
                url = url.Insert(url.LastIndexOf("/"), "/raw");
            }
            const string n = "\n";

            using (WebClient client = new WebClient() { Encoding = encoding }) {
                client.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)"); //pretend to be web browser https://stackoverflow.com/a/40275488

                string page = null;

                try {
                    page = client.DownloadString(url);
                } catch (Exception e) {
                    Console.WriteLine($"Error downloading {url}. {e.Message}");
                    if (retryCount >= retries.Length - 1) { retryCount = 0; return null; }
                    return DownloadBlockList(url, retries[retryCount++]);
                }

                retryCount = 0;

                page = page.Replace("\n\r", "\n").Replace(Environment.NewLine, "\n"); //normalize line endings
                page = Regex.Replace(page, @"[ \t]+", " ");

                var sample = page.Substring(0, page.Length > 500 ? 500 : page.Length);

                if (sample.StartsWith("[Adblock") || GetOccuranceCount(sample, "||") > 3 || GetOccuranceCount(sample, "!") > 3) {
                    Console.WriteLine(results + "Format: Adblock");
                    return page.Split(n);
                }

                if (GetOccuranceCount(sample, "http://") > 5 || GetOccuranceCount(sample, "https://") > 5) {
                    Console.WriteLine(results + "Format: URL");
                    return page.Split(n);
                }

                if (sample.ContainsAny("^")) {
                    Console.WriteLine(results + "Format: Regex");
                    return page.Split(n);
                }

                Console.WriteLine(results + "Format: Domains");

                return page.Split(n).Select(line => {
                    line = line.Trim();
                    if (!line.Exists() || line.StartsWith('#') || line.StartsWith("::")) return line;
                    var parts = line.Split(' ');
                    var domain = parts.Length == 1 ? line : parts[1];
                    if (domain.Contains('.') || domain.Contains(':')) { } else {
                        if (!domain.StartsWith("#")) {
                            SkipList.Add($"{Hole} {domain}");
                            //Console.WriteLine("Skipping: " + domain);
                        }

                        return "";
                    }
                    return $"{Hole} {domain}";
                });
            }
        }

        private static string GetUrlFromNodePath(string path) {
            var url = path.Substring("hosts\\".Length);
            var scheme = url.Substring(0, url.IndexOf("\\"));
            url = url.Substring(url.IndexOf("\\") + 1).Replace("\\", "/");
            url = $"{scheme}://{url}";

            return url;
        }

        private static void Main(string[] args) {
            Directory.SetCurrentDirectory(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory).Parent.Parent.Parent.FullName);

            //using (Timer t = new Timer()) {
            Console.WriteLine("Parsing BlockList...");
            BlockLists = File.ReadAllLines(BlockListPath).AsParallel().Select(StringHelper.NormalizeUrl).ToHashSet();
            BlockLists.Remove(string.Empty);
            Console.WriteLine($"Blocklists: Count={BlockLists.Count()}:{Environment.NewLine}");
            //}

            BackupList = new SortedSet<string>(File.Exists(BackupListPath) ? File.ReadAllLines(BackupListPath) : new string[0]);
            MissingList = new SortedSet<string>(File.Exists(DeadListPath) ? File.ReadAllLines(DeadListPath) : new string[0]);

            Console.WriteLine("Building Tree...");
            TreeNode<string> blockTree = BuildTree(BlockLists, "hosts");
            //blockTree.Print();

            Console.WriteLine("Working...");
            ArchiveLists(blockTree);

            //check to see if any missing lists have returned
            if (shouldUpdate) {
                if (BackupList.Count() > 0) {
                    ArchiveLists(BuildTree(BackupList, "hosts"));
                }
                if (MissingList.Count() > 0) {
                    ArchiveLists(BuildTree(MissingList, "hosts"));
                }
            }

            File.WriteAllLines(BlockListPath, GoodList); //lists which are active
            File.WriteAllLines(BackupListPath, BackupList); //no longer active, but we have a copy
            File.WriteAllLines(DeadListPath, MissingList); //no longer active and no copy

            File.WriteAllLines(SkipListPath, SkipList);


            using (var status = new StreamWriter(StatusPath)) {
                status.WriteLine("Pi-Hosts Status Report");
                status.WriteLine($"Last Update: {DateTime.Today.ToShortDateString()}");
                status.WriteLine($"Active Lists: {GoodList.Count()}");
                status.WriteLine($"Backup Lists: {BackupList.Count()}");
                status.WriteLine($"Dead   Lists: {MissingList.Count()}");
                status.WriteLine($"Domain Count: {domainCount.ToString()}");
            }
        }

        private static void MarkListAs(string list, ListType type) {
            switch (type) {
                default:
                case ListType.Good:
                case ListType.Cached:
                    lock (GoodList) GoodList.Add(list);
                    return;

                case ListType.BackedUp:
                    lock (BackupList) BackupList.Add(list);
                    return;

                case ListType.Missing:
                    lock (MissingList) MissingList.Add(list);
                    return;
            }
        }

        private static ListType WriteListToFile(string path, string url) {
            var dnld = DownloadBlockList(url, Encoding.Default);
            if (dnld != null && dnld.Count() > 0) {
                File.WriteAllLines(path, dnld);
                FileInfo file = new FileInfo(path);
                if (BytesToMebibytes(file.Length) > MaxFileSizeMb) {
                    Console.WriteLine($"File too large ({BytesToMebibytes(file.Length)}) ({url})");
                    file.SplitFile(MaxFileSizeMb * MIB);
                    File.Delete(file.FullName);
                }
                return ListType.Good;
            } else {
                return ListType.Missing;
            }
        }
    }

    internal class Timer : IDisposable {
        private Stopwatch s;

        public Timer() => s = Stopwatch.StartNew();

        public void Dispose() {
            s.Stop();
            Console.WriteLine($"Time taken {s.ElapsedMilliseconds}ms");
        }
    }
}

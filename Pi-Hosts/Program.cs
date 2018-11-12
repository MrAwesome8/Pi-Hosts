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

    internal class Timer : IDisposable {
        private Stopwatch s;

        public Timer() => s = Stopwatch.StartNew();

        public void Dispose() {
            s.Stop();
            Console.WriteLine($"Time taken {s.ElapsedMilliseconds}ms");
        }
    }

    public enum ListType {
        Good,
        Cached,
        BackedUp,
        Missing
    }

    internal static class Program {
        private const string BlockListPath = "block.list";
        private const string DeadListPath = "dead.list";
        private const string BackupListPath = "backup.list";
        private static ISet<string> GoodList = new SortedSet<string>();
        private static ISet<string> BackupList;
        private static ISet<string> MissingList;
        private static ISet<string> BlockLists;
        private const string Hole = "0.0.0.0";
        private const int MaxFileSize = 80; //github maxfile upload is 100MB. (rec.50MB)

        private static TreeNode<string> BuildTree() {
            TreeNode<string> blockTree = new TreeNode<string>("hosts");

            BlockLists.AsParallel().ForAll(list => {
                var url = new Uri(list);

                var segments = url.Segments.Where(x => !x.Equals("/") && !string.IsNullOrEmpty(x) && !string.IsNullOrWhiteSpace(x)).ToArray();

                if (!string.IsNullOrEmpty(url.Query)) {
                    if (segments.Count() > 0) {
                        segments[segments.Length - 1] += url.Query;
                    }
                }

                TreeNode<string> curNode;
                lock (blockTree) {
                    curNode = blockTree.AddChild(url.Scheme);
                }
                curNode = curNode.AddChild(url.Host);
                foreach (var node in segments) {
                    curNode = curNode.AddChild(node.Replace("/", ""));
                }

                //curNode = curNode.AddChild(url.Query);
            });

            return blockTree;
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

        private const int MIB = 1049000;

        private static double BytesToMebibytes(long bytes) => bytes / (double)MIB;

        private static int GetOccuranceCount(string src, string sub) => (src.Length - src.Replace(sub, "").Length) / sub.Length;

        private static Encoding[] retries = new[] { Encoding.UTF8, Encoding.Unicode, Encoding.ASCII, Encoding.UTF32, Encoding.UTF7, Encoding.BigEndianUnicode };
        private static int retryCount = 0;

        private static IEnumerable<string> DownloadBlockList(string url, Encoding encoding) {
            string results = $"Attempting to download: {url}...";

            if (!url.IsValid()) {
                Console.WriteLine(results + "Failed");
                return null;
            }
            if (url.Contains("github.com") && !url.Contains("/raw")) {
                url = GetRawGithub(url);
            }
            string n = "\n";

            using (WebClient client = new WebClient() { Encoding = encoding }) {
                client.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)"); //pretend to be web browser https://stackoverflow.com/a/40275488

                string page = null;

                try {
                    page = client.DownloadString(url);
                } catch (Exception) {
                    if (retryCount >= retries.Length - 1) {
                        return null;
                    }
                    return DownloadBlockList(url, retries[retryCount++]);
                }

                retryCount = 0;

                page = page.Replace("\n\r", "\n").Replace(Environment.NewLine, "\n");
                page = Regex.Replace(page, @"[ \t]+", " ");

                var sample = page.Substring(0, page.Length > 500 ? 500 : page.Length);
                if (sample.StartsWith("[Adblock") || GetOccuranceCount(sample, "||") > 3 || GetOccuranceCount(sample, "!") > 3) {
                    //probably an adblock filter, we don't want to touch it
                    Console.WriteLine(results + "Format: Adblock");

                    return page.Split(n);
                }

                if (GetOccuranceCount(sample, "http://") > 5 || GetOccuranceCount(sample, "https://") > 5) {
                    //probably a url filter, we don't want to touch it
                    Console.WriteLine(results + "Format: URL");

                    return page.Split(n);
                }

                Console.WriteLine(results + "Format: Domains");

                return page.Split(n/*, StringSplitOptions.RemoveEmptyEntries*/).Select(line => {
                    line = line.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return line;
                    var prot = line.Split(' ');

                    var lk = prot.Length == 1 ? line : prot[1];
                    return $"{Hole} {lk}";
                });
            }
        }

        private static void WriteListToFile(string path, string url) {
            var dnld = DownloadBlockList(url, Encoding.Default);
            if (dnld != null && dnld.Count() > 0) {
                File.WriteAllLines(path, dnld);
                FileInfo file = new FileInfo(path);
                if (BytesToMebibytes(file.Length) > MaxFileSize) {
                    Console.WriteLine($"File too large ({BytesToMebibytes(file.Length)}) ({url})");
                    file.SplitFile(MaxFileSize * MIB);
                    File.Delete(file.FullName);
                }
                MarkListAs(url, ListType.Good);
            } else {
                MarkListAs(url, ListType.Missing);
            }
        }

        private static void Main(string[] args) {
            Directory.SetCurrentDirectory(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory).Parent.Parent.Parent.FullName);

            //using (Timer t = new Timer()) {
            Console.WriteLine("Parsing BlockList...");
            BlockLists = File.ReadAllLines(BlockListPath).AsParallel().Select(StringHelper.NormalizeUrl).ToHashSet();
            BlockLists.Remove(string.Empty);
            Console.WriteLine($"Blocklists: Count={BlockLists.Count()}:\n");
            //}

            BackupList = new SortedSet<string>(File.Exists(BackupListPath) ? File.ReadAllLines(BackupListPath) : new string[0]);
            MissingList = new SortedSet<string>(File.Exists(DeadListPath) ? File.ReadAllLines(DeadListPath) : new string[0]);

            Console.WriteLine("Building Tree...");
            TreeNode<string> blockTree = BuildTree();

            //blockTree.Print();

            var shouldUpdateLists = true;

            Console.WriteLine("Working...");

            List<TreeNode<string>> leafs = new List<TreeNode<string>>();
            blockTree.ForEachNode(node => {
                if (!node.HasChildren) {
                    leafs.Add(node);
                }
            });

            leafs./*AsParallel().ForAll*/ForEach(node => {
                string path = BuildPath(node);

                var url = path.Substring("hosts\\".Length);
                var scheme = url.Substring(0, url.IndexOf("\\"));
                url = url.Substring(url.IndexOf("\\") + 1).Replace("\\", "/");
                url = $"{scheme}://{url}";

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
                    if (file.Exists) {
                        if (valid) {
                            if (shouldUpdateLists) {
                                WriteListToFile(path, url);
                            } else {
                                MarkListAs(url, ListType.Cached);
                            }
                        } else {
                            MarkListAs(url, ListType.BackedUp);
                        }
                    } else {
                        if (!valid) {
                            //url doesnt exist and we have no backup :(
                            MarkListAs(url, ListType.Missing);
                            return;
                        }

                        //create path
                        file.Directory.Create();
                        using (File.Create(path)) { }

                        //download content
                        WriteListToFile(path, url);
                    }
                } catch (Exception) {
                    MarkListAs(url, ListType.Missing);
                }
            });

            File.WriteAllLines(BlockListPath, GoodList); //lists which are active
            File.WriteAllLines(BackupListPath, BackupList); //no longer active, but we have a copy
            File.WriteAllLines(DeadListPath, MissingList); //no longer active and no copy
        }
    }
}

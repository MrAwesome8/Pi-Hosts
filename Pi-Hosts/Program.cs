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

        private static TreeNode<string> BuildTree() {
            TreeNode<string> blockTree = new TreeNode<string>("hosts");

            BlockLists.AsParallel().ForAll(list => {
                var url = new Uri(list);

                var segments = url.Segments.Where(x => !x.Equals("/") && !string.IsNullOrEmpty(x) && !string.IsNullOrWhiteSpace(x));

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
                    GoodList.Add(list);
                    return;

                case ListType.BackedUp:
                    BackupList.Add(list);
                    return;

                case ListType.Missing:
                    MissingList.Add(list);
                    return;
            }
        }

        private static int GetOccuranceCount(string src, string sub) => (src.Length - src.Replace(sub, "").Length) / sub.Length;

        private static IEnumerable<string> DownloadBlockList(string url) {
            Console.Write($"Attempting to download: {url}...");
            if(!url.IsValid()) {
                Console.WriteLine("Failed.");
                return null;
            }
            if (url.Contains("github.com") && !url.Contains("/raw")) {
                url = GetRawGithub(url);
            }
            string n = "\n";

            using (WebClient client = new WebClient() { Encoding = Encoding.UTF8 }) {
                client.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)"); //pretend to be web browser https://stackoverflow.com/a/40275488

                var page = client.DownloadString(url);
                page = page.Replace("\t", "").Replace("\n\r", "\n").Replace(Environment.NewLine, "\n");
                page= Regex.Replace(page, @"[ ]{2,}", "");

                var sample = page.Substring(0, 500);
                if (sample.StartsWith("[Adblock") || GetOccuranceCount(sample,"||")>3|| GetOccuranceCount(sample, "!") > 3) {
                    //probably an adblock filter, we don't want to touch it
                    Console.WriteLine("Format: Adblock");
                    return page.Split(n);
                }

                if (GetOccuranceCount(sample, "http://") > 5 || GetOccuranceCount(sample, "https://") > 5) {
                    //probably a url filter, we don't want to touch it
                    Console.WriteLine("Format: URL");
                    return page.Split(n);
                }

                Console.WriteLine("Format: Domains");


                return page.Split(n/*, StringSplitOptions.RemoveEmptyEntries*/).Select(line => {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return line;
                    var prot = line.Split(' ');

                    var lk = prot.Length == 1 ? line : prot[1];
                    return $"{Hole} {lk}";
                });
            }
        }
        
        private static void Main(string[] args) {
            Directory.SetCurrentDirectory(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory).Parent.Parent.Parent.FullName);

            using (Timer t = new Timer()) {
                Console.WriteLine("Parsing BlockList...");
                BlockLists = new SortedSet<string>(File.ReadAllLines(BlockListPath).AsParallel().Select(StringHelper.NormalizeUrl));//.ToHashSet();
                BlockLists.Remove(string.Empty);
                Console.WriteLine($"Blocklists: Count={BlockLists.Count()}:\n");
                File.WriteAllLines(BlockListPath, BlockLists); //lists which are active
            }
            return;
            BackupList = new SortedSet<string>(File.Exists(BackupListPath) ? File.ReadAllLines(BackupListPath) : new string[0]);
            MissingList = new SortedSet<string>(File.Exists(DeadListPath)?File.ReadAllLines(DeadListPath):new string[0]);

            Console.WriteLine("Building Tree...");
            TreeNode<string> blockTree = BuildTree();

            blockTree.Print();

            var shouldUpdateLists = false;

            Console.WriteLine("Working...");

            blockTree.ForEachNode(node => {
                if (!node.HasChildren) {
                    string path = BuildPath(node);

                    var url = path.Substring("hosts\\".Length);
                    var scheme = url.Substring(0, url.IndexOf("\\"));
                    url = url.Substring(url.IndexOf("\\") + 1).Replace("\\", "/");
                    url = $"{scheme}://{url}";

                    bool valid = url.IsValid();
                    if (Directory.Exists(path)) {
                        if (valid) {
                            if (shouldUpdateLists) {
                                var dnld = DownloadBlockList(url);
                                if(dnld!=null && dnld.Count()>0) {
                                    File.WriteAllLines(path, dnld);
                                    MarkListAs(url, ListType.Good);
                                } else {
                                    MarkListAs(url, ListType.BackedUp);
                                }
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
                        FileInfo file = new FileInfo(path);
                        file.Directory.Create();
                        using (File.Create(path)) ;

                        //download content
                        var dnld = DownloadBlockList(url);
                        if (dnld != null && dnld.Count() > 0) {
                            File.WriteAllLines(path, dnld);
                            MarkListAs(url, ListType.Good);
                        } else {
                            MarkListAs(url, ListType.BackedUp);
                        }
                    }
                }
            });

            File.WriteAllLines(BlockListPath, GoodList); //lists which are active
            File.WriteAllLines(BackupListPath, BackupList); //no longer active, but we have a copy
            File.WriteAllLines(DeadListPath, MissingList); //no longer active and no copy
        }
    }
}

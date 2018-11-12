using System;
using System.Collections.Generic;
using System.IO;

namespace Pi_Hosts {

    internal static class StringHelper {

        public static bool IsValid(this string url) {
            using (var client = new MyClient()) {
                client.HeadOnly = true;
                try {
                    client.DownloadString(url);
                    return true;
                } catch (Exception e) {
                    //Console.WriteLine($"{e.Message} on {url}");
                    return false;
                }
            }
        }

        public static bool Exists(this string value) => !string.IsNullOrEmpty(value) && !string.IsNullOrWhiteSpace(value);

        public static string NormalizeUrl(this string url) {
            url = url.Trim().TrimEnd('/');
            //var uri = new Uri(url);
            if (/*uri.Scheme*/url.Contains("http://")) {
                var tryHttps = url.Replace("http://", "https://");
                if (IsValid(tryHttps)) { url = tryHttps; }
            }

            if (/*uri.Host*/url.Contains("www.")) {
                var tryWithoutWww = url.Replace("www.", "");
                if (IsValid(tryWithoutWww)) { url = tryWithoutWww; }
            }

            if (url.Contains("github") && (url.Contains("/raw") || url.Contains("raw."))) {
                url = GetNormalGithub(url);
            }

            return url;
        }

        public static string GetNormalGithub(string raw) {
            if (raw.Contains("gist.githubusercontent")) {
                return raw.Substring(0, raw.IndexOf("/raw/")).Replace("gist.githubusercontent", "gist.github");
            }
            return raw.Replace("raw.githubusercontent", "github").Replace("/master", "/blob/master");
        }

        public static string GetRawGithub(string normal) {
            if (normal.Contains("gist.github")) {
                return $"{normal}/raw";
            }
            return normal.Replace("blob/", "raw/");
        }

        //https://stackoverflow.com/a/16901426
        public static void SplitFile(this FileInfo file, int chunkSize) {
            byte[] buffer = new byte[chunkSize];
            List<byte> extraBuffer = new List<byte>();

            string path = file.FullName;
            int extensionIndex = path.IndexOf(file.Extension);
            int insertIndex = extensionIndex == 0 ? path.Length : extensionIndex;

            using (Stream input = File.OpenRead(path)) {
                int index = 0;
                while (input.Position < input.Length) {
                    Console.WriteLine($"Splitting {file.Name} into Part{index}");
                    var newName = path.Insert(insertIndex, (index + 1).ToString());
                    using (Stream output = File.Create(newName)) {
                        int chunkBytesRead = 0;
                        while (chunkBytesRead < chunkSize) {
                            int bytesRead = input.Read(buffer, chunkBytesRead, chunkSize - chunkBytesRead);

                            if (bytesRead == 0) { break; }

                            chunkBytesRead += bytesRead;
                        }

                        byte extraByte = buffer[chunkSize - 1];
                        while (extraByte != '\n') {
                            int flag = input.ReadByte();
                            if (flag == -1)
                                break;
                            extraByte = (byte)flag;
                            extraBuffer.Add(extraByte);
                        }

                        output.Write(buffer, 0, chunkBytesRead);
                        if (extraBuffer.Count > 0)
                            output.Write(extraBuffer.ToArray(), 0, extraBuffer.Count);

                        extraBuffer.Clear();
                    }
                    ++index;
                }
            }
        }
    }
}

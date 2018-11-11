using System;

namespace Pi_Hosts {
    static class StringHelper {

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
            url = url.Trim();
            //var uri = new Uri(url);
            if (/*uri.Scheme*/url.Contains("http://")) {
                var tryHttps = url.Replace("http://", "https://");
                if (IsValid(tryHttps)) { url = tryHttps; }
            }

            if (/*uri.Host*/url.Contains("www.")) {
                var tryWithoutWww = url.Replace("www.", "");
                if (IsValid(tryWithoutWww)) { url = tryWithoutWww; }
            }

            if (url.Contains("github")&& (url.Contains("/raw") || url.Contains("raw."))) {
                url = GetNormalGithub(url);
            }

            return url;
        }

        public static string GetNormalGithub(string raw) {
            if(raw.Contains("gist.githubusercontent")) {
                return raw.Substring(0,raw.IndexOf("/raw/")).Replace("gist.githubusercontent", "gist.github");
            }
            return raw.Replace("raw.githubusercontent", "github").Replace("/master", "/blob/master");
        }

        public static string GetRawGithub(string normal) {
            if (normal.Contains("gist.github")) {
                return $"{normal}/raw";
            }
            return normal.Replace("blob/", "raw/");
        }
    }
}

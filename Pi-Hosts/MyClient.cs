using System;
using System.Net;

namespace Pi_Hosts {
    //https://stackoverflow.com/a/156750
    internal class MyClient : WebClient {
        public bool HeadOnly { get; set; }

        protected override WebRequest GetWebRequest(Uri address) {
            WebRequest req = base.GetWebRequest(address);
            if (HeadOnly && req.Method == "GET") {
                req.Method = "HEAD";
            }
            return req;
        }
    }
}

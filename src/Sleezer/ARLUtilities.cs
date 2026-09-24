using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.XPath;
using DeezNET;
using NLog;
using NzbDrone.Plugin.Sleezer.Core.Deezer;

namespace NzbDrone.Plugin.Sleezer.Deezer
{
    public static class ARLUtilities
    {
        private const string FIREHAWK_URL = "https://rentry.org/firehawk52";
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan ArlValidationTimeout = TimeSpan.FromSeconds(30);

        public static async Task<string> GetFirstValidARL()
        {
            using HttpClient client = new();
            var html = await client.GetStringAsync(FIREHAWK_URL);

            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(html);

            var deezerTitleNode = (IElement)document.Body.SelectSingleNode("//*[@id=\"deezer-arls\"]");

            var tableNode = deezerTitleNode.NextElementSibling;
            while (tableNode != null && tableNode.GetAttribute("class") != "ntable-wrapper")
                tableNode = tableNode.NextElementSibling;

            if (tableNode == null)
                return "";
            else
                tableNode = (IElement)tableNode.SelectSingleNode("table/tbody");

            List<string> arls = new();
            foreach (var row in tableNode.ChildNodes)
            {
                if (row is IElement elementRow)
                {
                    var tokenElement = elementRow.QuerySelector("td:nth-child(4) code");
                    var token = tokenElement?.TextContent;
                    if (token != null)
                    {
                        if (IsValid(token))
                            return token;
                    }
                }
            }

            return "";
        }

        // Validates on a throwaway client: SetARL on the live one would swap its account under in-flight work.
        public static bool IsValid(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            try
            {
                var client = new DeezerClient();
                if (!client.SetARL(token).Wait(ArlValidationTimeout))
                {
                    _logger.Debug("ARL validation timed out after {Seconds}s; treating as invalid", ArlValidationTimeout.TotalSeconds);
                    return false;
                }

                return DeezerArlCheck.HasSignedInUser(client.GWApi.ActiveUserData);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "ARL validation failed");
                return false;
            }
        }
    }
}

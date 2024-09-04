using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Data.SQLite;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace podcastagent
{
    public class Podcast
    {
        public readonly string Id;
        public string Title;
        public string Description;
        public string PodcastURL;

        public Podcast(string title, string description, string podcastURL)
        {
            Title = title;
            Description = description;
            PodcastURL = podcastURL;
        }
    }

    public class Episode
    {
        //public int PodcastID;
        public string PublishDate;
        public string Title;
        public string Guid;
        public string MediaURL;

        public Episode(string publishDate, string title, string guid, string mediaURL)
        {
            PublishDate = publishDate;
            Title = title;
            Guid = guid;
            MediaURL = mediaURL;
        }

        public override string ToString()
        {
            return string.Format("Episode('{0}','{1}', '{2}', {3})", PublishDate, Title, Guid, MediaURL);
        }

        public override bool Equals(object obj)
        {
            if ( obj is Episode )
                return Guid == (obj as Episode).Guid;
            return false;
        }

        public override int GetHashCode()
        {
            return Guid.GetHashCode();
        }
    }

    class Program
    {
        const string connectionString = @"URI=file:podcastagent_db";
        const int maxEpisodesPerPodcast = 3;

        static int downloadPercent = 0;
        static int downloadCount = 0;

        static void Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "build")
            {
                BuildDatabase();


            }
            else
            {
                // For each podcast subscription

                List<string> podcastURLs = new List<string>();
                podcastURLs.Add("http://feeds.feedburner.com/netRocksFullMp3Downloads");
                podcastURLs.Add("http://www.npr.org/templates/rss/podlayer.php?id=15709577");



                // Continue with next podcast subscription
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        public static void BuildDatabase()
        {
            Console.Write("Building database... ");
            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                using (SQLiteCommand cmd = new SQLiteCommand(conn))
                {
                    cmd.CommandText = "DROP TABLE IF EXISTS Podcasts";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "CREATE TABLE Podcasts (Id INTEGER PRIMARY KEY, Title TEXT, Description TEXT, PodcastURL TEXT)";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "DROP TABLE IF EXISTS Episodes";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "CREATE TABLE Episodes (Id INTEGER PRIMARY KEY, PodcastId INTEGER, PublishDate TEXT, Title TEXT, Guid TEXT, MediaURL TEXT)";
                    cmd.ExecuteNonQuery();

                }
                conn.Close();
            }
            Console.WriteLine("complete.");
        }


        public static void UpdateSubscription(Podcast podcast)
        {
            // Load the list of existing episodes from the database
            IEnumerable<Episode> existingEpisodes = GetExistingEpisodes(podcast);

            // Retrieve the current episodes from the podcast feed
            IEnumerable<Episode> currentEpisodes = RetrieveCurrentEpisodeList(podcast);

            // Any current episodes that are not in the existing 
            // list are new. Store these in the database.
            List<Episode> newEpisodes = currentEpisodes.ToList<Episode>();
            foreach (Episode episode in currentEpisodes)
            {
                if (existingEpisodes.Contains<Episode>(episode))
                    newEpisodes.Remove(episode);
            }
            Console.WriteLine(string.Format("{0} new episodes were found.", newEpisodes.Count));

            // Add the new episodes to the database
            if (newEpisodes.Count > 0)
            {
                AddNewEpisodes(newEpisodes);
            }

            // Now retrieve the full list of episodes and download. This ensures
            // that the number of available episodes is always at least
            // maxEpisodesPerPodcast, even if it changes or if episodes were deleted.
            currentEpisodes = RetrieveCurrentEpisodeList(podcast);
            IEnumerable<Episode> downloadedEpisodes = DownloadEpisodes(currentEpisodes);

            // Update MP3 tags only for the episodes downloaded
            //UpdateMP3Tags(downloadedEpisodes);

        }

        public static IEnumerable<Episode> RetrieveCurrentEpisodeList(Podcast podcast)
        {
            // Retrieve the podcast feed XML document
            WebRequest request = WebRequest.Create(podcast.PodcastURL);
            WebResponse response = request.GetResponse();

            Stream dataStream = response.GetResponseStream();
            StreamReader reader = new StreamReader(dataStream);
            string feedXML = reader.ReadToEnd();

            reader.Close();
            dataStream.Close();
            response.Close();

            // Parse the feed for the list of podcast episodes
            XDocument doc = XDocument.Parse(feedXML);
            IEnumerable<Episode> currentEpisodes = 
                from e in doc.Descendants("item")
                select new Episode( (string)e.Element("pubDate").Value, 
                                    (string)e.Element("title").Value,
                                    (string)e.Element("guid").Value,
                                    (string)e.Element("enclosure").Attribute("url"));
            return currentEpisodes;
        }

        public static IEnumerable<Episode> GetExistingEpisodes(Podcast podcast)
        {
            List<Episode> existingEpisodes = new List<Episode>();
            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                string statement = string.Format("SELECT Id, PublishDate, Title, Guid, MediaURL FROM Episodes WHERE PodcastId = {0}", podcast.Id);
                using (SQLiteCommand cmd = new SQLiteCommand(statement, conn))
                {
                    using (SQLiteDataReader reader = cmd.ExecuteReader())
                    {
                        while(reader.Read())
                        {
                            Episode episode = new Episode(
                                reader.GetString(1), // PublishDate
                                reader.GetString(2), // Title
                                reader.GetString(3), // Guid
                                reader.GetString(4)); // Downloaded
                            existingEpisodes.Add(episode);
                        }
                        reader.Close();
                    }
                }
                conn.Close();
            }
            return existingEpisodes;
        }

        public static void AddNewEpisodes(IEnumerable<Episode> newEpisodes)
        {
            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                using (SQLiteCommand cmd = new SQLiteCommand(conn))
                {
                    foreach (Episode episode in newEpisodes)
                    {
                        cmd.CommandText = string.Format("INSERT INTO Episodes VALUES (null, '{0}', '{1}', '{2}', '{3}')", 
                            episode.PublishDate, episode.Title, episode.Guid, episode.MediaURL);
                        cmd.ExecuteNonQuery();
                    }
                }
                conn.Close();
            }

            Console.WriteLine(string.Format("Added {0} new episodes to database.", newEpisodes.Count()));
        }

        public static IEnumerable<Episode> DownloadEpisodes(IEnumerable<Episode> episodes)
        {
            downloadCount = 0;
            List<Episode> downloadedEpisodes = new List<Episode>();
            foreach( Episode episode in episodes )
            {
                string fileName = GetFileName(episode.MediaURL);
                if (!File.Exists(fileName))
                {
                    //DownloadEpisode(episode).Wait();
                    Console.WriteLine("*** <placeholder> download file '{0}'", episode.MediaURL);
                    downloadedEpisodes.Add(episode);
                    downloadCount++;
                }
                else
                {
                    Console.WriteLine("File '{0}' already downloaded, skipping.", fileName);
                    downloadCount++;
                }
                if (downloadCount >= maxEpisodesPerPodcast)
                    break;
            }
            return downloadedEpisodes;
        }

        public static async Task DownloadEpisode(Episode episode)
        {
            Console.WriteLine("Downloading file '{0}' ...", episode.MediaURL);
            using (WebClient wc = new WebClient())
            {
                downloadPercent = 0;
                wc.DownloadProgressChanged += Wc_DownloadProgressChanged;
                wc.DownloadFileCompleted += Wc_DownloadFileCompleted;

                await wc.DownloadFileTaskAsync(new System.Uri(episode.MediaURL), GetFileName(episode.MediaURL));
            }
        }

        public static string GetFileName(string uri)
        {
            string[] parts = uri.Split('/');
            string fileName = "";
            if (parts.Length > 0)
                fileName = parts[parts.Length - 1];
            return fileName;
        }

        private static void Wc_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            if ( e.ProgressPercentage / 10 != downloadPercent / 10 )
            {
                downloadPercent = e.ProgressPercentage;
                Console.Write("{0}% ", e.ProgressPercentage);
            }
        }

        private static void Wc_DownloadFileCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            Console.WriteLine("... download complete.");
        }

    }
}

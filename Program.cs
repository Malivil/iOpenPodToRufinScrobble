using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace iOpenPodToRufinScrobble
{
    internal class SongData 
    {
        public string SongName { get; set; }
        public string AlbumName { get; set; }
        public string ArtistName { get; set; }
        public int PlayCount { get; set; }
    }

    public class Program
    {
        private static IConfiguration configuration;
        private static JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        private const byte DATASET_TYPE_SONG = 1;

        private const long MHOD_SONG_NAME = 1;
        private const long MHOD_ALBUM_NAME = 3;
        private const long MHOD_ARTIST_NAME = 4;

        public static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                PrintAndPause($"USAGE: {Assembly.GetExecutingAssembly().GetName().Name} itunesdb_path");
                return;
            }

            var builder = new ConfigurationBuilder()
                          .SetBasePath(AppContext.BaseDirectory)
                          .AddJsonFile("appsettings.json", true, true);
            configuration = builder.Build();

            var itunesDbPath = args[0];
            Console.WriteLine("Syncing play counts from: " + itunesDbPath);

            var songDataList = new List<SongData>();
            if (!ParseSongData(songDataList, itunesDbPath) || !ScrobbleToRufin(songDataList))
            {
                Console.ReadKey();
                Environment.Exit(1);
                return;
            }

            if (configuration.GetValue<bool>("OutputJson"))
            {
                Console.WriteLine("Writing song data to JSON file...");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(itunesDbPath), "song_data.json"), JsonConvert.SerializeObject(songDataList, jsonSerializerSettings));
            }

            PrintAndPause("Done!");
        }

        private static bool ParseSongData(List<SongData> songDataList, string itunesDbPath)
        {
            Console.WriteLine("Parsing iTunesDB file...");
            try
            {
                var text = File.ReadAllText(itunesDbPath);
                var database = JsonConvert.DeserializeObject<dynamic>(text);
                var topChildren = database.children;
                foreach (var child in topChildren)
                {
                    if (child.header == null || child.header.dataset_type != DATASET_TYPE_SONG) continue;

                    foreach (var song in child.children[0].children)
                    {
                        var songData = new SongData
                        {
                            PlayCount = song.header.play_count_1 + song.header.play_count_2
                        };
                        foreach (var field in song.children)
                        {
                            switch ((long)field.header.mhod_type)
                            {
                                case MHOD_SONG_NAME:
                                    songData.SongName = field.payload.value.Value;
                                    break;
                                case MHOD_ALBUM_NAME:
                                    songData.AlbumName = field.payload.value.Value;
                                    // Drop the numbers added to make it unique
                                    if (songData.AlbumName.StartsWith("Unknown Album"))
                                    {
                                        songData.AlbumName = "Unknown Album";
                                    }
                                    break;
                                case MHOD_ARTIST_NAME:
                                    songData.ArtistName = field.payload.value.Value;
                                    break;
                            }
                        }
                        songDataList.Add(songData);
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error reading iTunesDB file: {ex}");
                return false;
            }

            return true;
        }

        private static bool ScrobbleToRufin(List<SongData> songDataList)
        {
            Console.WriteLine($"Syncing {songDataList.Count} play counts to Rufin...");

            var rufinDataPath = configuration.GetValue<string>("RufinDataPath").Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (string.IsNullOrWhiteSpace(rufinDataPath))
            {
                PrintError("Rufin data path is not configured. Please check appsettings.json.");
                return false;
            }

            var rufinLibraryPath = Path.Combine(rufinDataPath, "store", "rufin-library.sqlite");
            if (!File.Exists(rufinLibraryPath))
            {
                PrintError($"Rufin catalog database not found at: {rufinLibraryPath}");
                return false;
            }

            var rufinLibraryWorkingPath = Path.Combine(Path.GetDirectoryName(rufinLibraryPath), "rufin-library.working.sqlite");
            var rufinLibraryBackupPath = Path.Combine(Path.GetDirectoryName(rufinLibraryPath), "rufin-library.backup.sqlite");

            try
            {
                File.Copy(rufinLibraryPath, rufinLibraryWorkingPath, true);

                using var catalogConnection = new SqliteConnection($"Data Source={rufinLibraryWorkingPath}");
                catalogConnection.Open();

                // Clear the existing listens table
                using var clearListensCommand = catalogConnection.CreateCommand();
                clearListensCommand.CommandText = "DELETE FROM listens";
                clearListensCommand.ExecuteNonQuery();

                // Listen parameters
                var listenId = Guid.NewGuid().ToString("N");
                var startedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var localPeriod = DateTime.Now.ToString("yyyy-MM");

                // Process each song
                var count = 0;
                foreach (var songData in songDataList)
                {
                    count++;
                    if (count % 100 == 0 || count == songDataList.Count)
                    {
                        Console.WriteLine($"Processed {count}/{songDataList.Count}");
                    }

                    // Find all the info we need for the listens
                    using var objectIdCommand = catalogConnection.CreateCommand();
                    objectIdCommand.CommandText = """
                        SELECT object_id, source_key, media_uri, title, display_artist, display_album, disc_number, track_number, year, release_date, source_format, duration_millis
                        FROM tracks
                        WHERE title = $songName COLLATE NOCASE
                          AND display_album = $albumName COLLATE NOCASE
                          AND display_artist = $artistName COLLATE NOCASE
                          AND object_id IS NOT NULL
                          AND source_key IS NOT NULL
                    """;
                    objectIdCommand.Parameters.AddWithValue("$songName", songData.SongName);
                    objectIdCommand.Parameters.AddWithValue("$albumName", songData.AlbumName);
                    objectIdCommand.Parameters.AddWithValue("$artistName", songData.ArtistName);

                    using var objectIdReader = objectIdCommand.ExecuteReader();

                    var objectId = string.Empty;
                    int? sourceKey = null;
                    var mediaUri = string.Empty;
                    var title = string.Empty;
                    var artist = string.Empty;
                    var album = string.Empty;
                    int? discNumber = null;
                    int? trackNumber = null;
                    int? year = null;
                    var releaseDate = string.Empty;
                    var sourceFormat = string.Empty;
                    int? duration = null;
                    while (objectIdReader.Read())
                    {
                        objectId = objectIdReader.GetString(0);
                        sourceKey = objectIdReader.IsDBNull(1) ? null : objectIdReader.GetInt32(1);
                        mediaUri = objectIdReader.GetString(2);
                        title = objectIdReader.GetString(3);
                        artist = objectIdReader.GetString(4);
                        album = objectIdReader.GetString(5);
                        discNumber = objectIdReader.IsDBNull(6) ? null : objectIdReader.GetInt32(6);
                        trackNumber = objectIdReader.IsDBNull(7) ? null : objectIdReader.GetInt32(7);
                        year = objectIdReader.IsDBNull(8) ? null : objectIdReader.GetInt32(8);
                        releaseDate = objectIdReader.IsDBNull(9) ? null : objectIdReader.GetString(9);
                        sourceFormat = objectIdReader.GetString(10);
                        duration = objectIdReader.IsDBNull(11) ? null : objectIdReader.GetInt32(11);
                    }

                    if (string.IsNullOrWhiteSpace(objectId) || !sourceKey.HasValue || string.IsNullOrWhiteSpace(mediaUri) ||
                        string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album) ||
                        !discNumber.HasValue || !trackNumber.HasValue ||
                        string.IsNullOrWhiteSpace(sourceFormat) || !duration.HasValue)
                    {
                        PrintWarning($"Could not find track data for '{songData.SongName}' by '{songData.ArtistName}' from '{songData.AlbumName}'");
                        continue;
                    }

                    // Find the source ID
                    using var sourceIdCommand = catalogConnection.CreateCommand();
                    sourceIdCommand.CommandText = """
                        SELECT object_id FROM sources WHERE source_key = $sourceKey
                    """;
                    sourceIdCommand.Parameters.AddWithValue("$sourceKey", sourceKey);

                    using var sourceIdReader = sourceIdCommand.ExecuteReader();
                    var sourceId = string.Empty;
                    if (sourceIdReader.Read())
                    {
                        sourceId = sourceIdReader.GetString(0);
                    }

                    if (string.IsNullOrWhiteSpace(sourceId))
                    {
                        PrintWarning($"Could not find source_id for source_key '{sourceKey}'");
                        continue;
                    }

                    // Update the native play count
                    using var updateCommand = catalogConnection.CreateCommand();
                    updateCommand.CommandText = """
                        UPDATE tracks
                        SET native_play_count = MAX(native_play_count, $playCount)
                        WHERE object_id = $id
                    """;
                    updateCommand.Parameters.AddWithValue("$playCount", songData.PlayCount);
                    updateCommand.Parameters.AddWithValue("$id", objectId);
                    updateCommand.ExecuteNonQuery();

                    using var existsCommand = catalogConnection.CreateCommand();
                    existsCommand.CommandText = """
                        SELECT *
                        FROM legacy_activity
                        WHERE track_object_id = $id
                    """;
                    existsCommand.Parameters.AddWithValue("$id", objectId);

                    using var existsReader = existsCommand.ExecuteReader();
                    var exists = false;
                    while (existsReader.Read())
                    {
                        exists = true;
                    }

                    if (exists)
                    {
                        using var activityCommand = catalogConnection.CreateCommand();
                        activityCommand.CommandText = """
                            UPDATE legacy_activity
                            SET play_count = MAX(play_count, $playCount)
                            WHERE track_object_id = $id
                        """;
                        activityCommand.Parameters.AddWithValue("$playCount", songData.PlayCount);
                        activityCommand.Parameters.AddWithValue("$id", objectId);
                        activityCommand.ExecuteNonQuery();
                    }
                    else
                    {
                        using var insertCommand = catalogConnection.CreateCommand();
                        insertCommand.CommandText = """
                            INSERT INTO legacy_activity (source_id, period, item_kind, track_object_id, play_count, skip_count, last_played_at, title, artist, album)
                            VALUES ($sourceKey, 'lifetime', 'track', $id, $playCount, 0, $lastPlayedAt, $title, $artist, $album)
                        """;
                        insertCommand.Parameters.AddWithValue("$sourceKey", sourceId);
                        insertCommand.Parameters.AddWithValue("$playCount", songData.PlayCount);
                        insertCommand.Parameters.AddWithValue("$id", objectId);
                        insertCommand.Parameters.AddWithValue("$lastPlayedAt", startedAt);
                        insertCommand.Parameters.AddWithValue("$title", title);
                        insertCommand.Parameters.AddWithValue("$artist", artist);
                        insertCommand.Parameters.AddWithValue("$album", album);
                        insertCommand.ExecuteNonQuery();
                    }
                }

                catalogConnection.Close();

                File.Move(rufinLibraryPath, rufinLibraryBackupPath, true);

                // Rename working files
                File.Move(rufinLibraryWorkingPath, rufinLibraryPath, true);
                File.Move(rufinLibraryWorkingPath + "-shm", rufinLibraryPath + "-shm", true);
                File.Move(rufinLibraryWorkingPath + "-wal", rufinLibraryPath + "-wal", true);

                // Delete the cache
                File.Delete(Path.Combine(rufinDataPath, "store", "rufin-library.image"));
            }
            catch (Exception ex)
            {
                PrintError($"Error syncing to Rufin database: {ex}");
                return false;
            }

            return true;
        }

        private static void PrintError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        private static void PrintWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(message);
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        private static void PrintAndPause(string msg)
        {
            Console.WriteLine(msg);
            Console.ReadKey();
        }
    }
}

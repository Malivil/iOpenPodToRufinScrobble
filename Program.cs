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

            var rufinCachePath = configuration.GetValue<string>("RufinCachePath").Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (string.IsNullOrWhiteSpace(rufinCachePath))
            {
                PrintError("Rufin cache path is not configured. Please check appsettings.json.");
                return false;
            }

            var rufinCatalogPath = Path.Combine(rufinCachePath, "store", "rufin-catalog.sqlite");
            if (!File.Exists(rufinCatalogPath))
            {
                PrintError($"Rufin catalog database not found at: {rufinCatalogPath}");
                return false;
            }

            var rufinCatalogWorkingPath = Path.Combine(Path.GetDirectoryName(rufinCatalogPath), "rufin-catalog.working.sqlite");
            var rufinCatalogBackupPath = Path.Combine(Path.GetDirectoryName(rufinCatalogPath), "rufin-catalog.backup.sqlite");

            try
            {
                File.Copy(rufinCatalogPath, rufinCatalogWorkingPath, true);

                using var catalogConnection = new SqliteConnection($"Data Source={rufinCatalogWorkingPath}");
                catalogConnection.Open();

                var count = 0;
                foreach (var songData in songDataList)
                {
                    count++;
                    if (count % 100 == 0)
                    {
                        Console.WriteLine($"Processed {count}/{songDataList.Count}");
                    }

                    using var objectIdCommand = catalogConnection.CreateCommand();
                    objectIdCommand.CommandText = """
                        SELECT object_id, source_key
                        FROM tracks
                        WHERE title = $songName COLLATE NOCASE
                          AND display_album = $albumName COLLATE NOCASE
                          AND display_artist = $artistName COLLATE NOCASE
                    """;
                    objectIdCommand.Parameters.AddWithValue("$songName", songData.SongName);
                    objectIdCommand.Parameters.AddWithValue("$albumName", songData.AlbumName);
                    objectIdCommand.Parameters.AddWithValue("$artistName", songData.ArtistName);

                    using var objectIdReader = objectIdCommand.ExecuteReader();

                    var objectId = string.Empty;
                    int? sourceKey = null;
                    while (objectIdReader.Read())
                    {
                        objectId = objectIdReader.GetString(0);
                        sourceKey = objectIdReader.GetInt32(1);
                    }

                    if (string.IsNullOrWhiteSpace(objectId) || !sourceKey.HasValue)
                    {
                        PrintWarning($"Could not find object_id for '{songData.SongName}' by '{songData.ArtistName}' from '{songData.AlbumName}'");
                        continue;
                    }

                    using var existsCommand = catalogConnection.CreateCommand();
                    existsCommand.CommandText = """
                        SELECT *
                        FROM activity_baseline
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
                        using var updateCommand = catalogConnection.CreateCommand();
                        updateCommand.CommandText = """
                            UPDATE activity_baseline
                            SET play_count = $playCount
                            WHERE track_object_id = $id
                        """;
                        updateCommand.Parameters.AddWithValue("$playCount", songData.PlayCount);
                        updateCommand.Parameters.AddWithValue("$id", objectId);
                        updateCommand.ExecuteNonQuery();
                    }
                    else
                    {
                        using var insertCommand = catalogConnection.CreateCommand();
                        insertCommand.CommandText = """
                            INSERT INTO activity_baseline (source_key, period, item_kind, track_object_id, play_count, skip_count)
                            VALUES ($sourceKey, 'lifetime', 'track', $id, $playCount, 0)
                        """;
                        insertCommand.Parameters.AddWithValue("$sourceKey", sourceKey);
                        insertCommand.Parameters.AddWithValue("$playCount", songData.PlayCount);
                        insertCommand.Parameters.AddWithValue("$id", objectId);
                        insertCommand.ExecuteNonQuery();
                    }
                }

                File.Move(rufinCatalogPath, rufinCatalogBackupPath, true);

                catalogConnection.Close();
                File.Move(rufinCatalogWorkingPath, rufinCatalogPath, true);
                
                // Cleanup temp files
                File.Delete(Path.Combine(Path.GetDirectoryName(rufinCatalogPath), "rufin-catalog.working.sqlite-shm"));
                File.Delete(Path.Combine(Path.GetDirectoryName(rufinCatalogPath), "rufin-catalog.working.sqlite-wal"));
            }
            catch (Exception ex)
            {
                PrintError($"Error syncing to Rufin databases: {ex}");
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

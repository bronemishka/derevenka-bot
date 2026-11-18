using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using Microsoft.Data.Sqlite;

namespace DerevenkaBot.Core.Infrastructure;

public sealed class DatabaseState
{
    public List<ImageSession> Sessions { get; set; } = [];
    public List<ActiveSession> Active { get; set; } = [];
    public List<ImageGeneration> Generations { get; set; } = [];
    public List<StoredAsset> Assets { get; set; } = [];
    public List<TelegramMessage> Messages { get; set; } = [];
    public List<ImageJob> Jobs { get; set; } = [];
    public List<UsageRecord> Usage { get; set; } = [];
    public List<Draft> Drafts { get; set; } = [];
    public List<HandledUpdate> Updates { get; set; } = [];
    public List<AccessGrant> AccessGrants { get; set; } = [];
    public List<PromptReply> Prompts { get; set; } = [];
    public List<AccessRequest> AccessRequests { get; set; } = [];
    public List<ChatConversation> Chats { get; set; } = [];
    public List<ChatRequest> ChatRequests { get; set; } = [];
    public static string ActorKey(Actor actor) => $"{actor.ChatId}:{actor.UserId}";
}
public sealed record HandledUpdate(string Id);
public sealed record AccessGrant(string Id, string Kind, long TargetId, bool Allowed, long GrantedBy, DateTimeOffset UpdatedAt);

// Small-group repository: an immediate SQLite transaction serializes reservations, leases and session changes.
// Payloads are versioned by schema migration; no prompt or image bytes are written to logs.
public sealed class ImageDatabase(ImageOptions options)
{
    private string ConnectionString
    {
        get
        {
            Directory.CreateDirectory(options.StoragePath);
            return new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(options.StoragePath, "images.db"), DefaultTimeout = 30 }.ToString();
        }
    }
    public void Migrate()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS ImageSchema(Version INTEGER PRIMARY KEY);";
        command.ExecuteNonQuery();
        using var tx = connection.BeginTransaction(deferred: false);
        command.Transaction = tx;
        foreach (var table in Tables)
        {
            command.CommandText = $"CREATE TABLE IF NOT EXISTS {table}(Id TEXT PRIMARY KEY, Payload TEXT NOT NULL CHECK(json_valid(Payload)));";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ImageJobs_Key ON ImageJobs(json_extract(Payload,'$.IdempotencyKey'));
            CREATE INDEX IF NOT EXISTS IX_ImageJobs_Status ON ImageJobs(json_extract(Payload,'$.Status'),json_extract(Payload,'$.NextAttemptAt'));
            CREATE INDEX IF NOT EXISTS IX_ImageGenerations_Session ON ImageGenerations(json_extract(Payload,'$.SessionId'));
            INSERT OR IGNORE INTO ImageSchema VALUES(1);
            INSERT OR IGNORE INTO ImageSchema VALUES(2);
            INSERT OR IGNORE INTO ImageSchema VALUES(3);
            INSERT OR IGNORE INTO ImageSchema VALUES(4);
            INSERT OR IGNORE INTO ImageSchema VALUES(5);
            INSERT OR IGNORE INTO ImageSchema VALUES(6);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ImageAccessRequests_PendingUser ON ImageAccessRequests(json_extract(Payload,'$.Applicant.UserId')) WHERE json_extract(Payload,'$.Status')=0;
            """;
        command.ExecuteNonQuery();
        tx.Commit();
    }
    private static readonly string[] Tables = ["ImageSessions", "ImageActiveSessions", "ImageGenerations", "ImageAssets",
        "ImageTelegramMessages", "ImageJobs", "ImageUsage", "ImageDrafts", "ImageUpdates", "ImageAccessGrants", "ImagePromptReplies", "ImageAccessRequests", "ImageChats", "ImageChatRequests"];
    public T Run<T>(Func<DatabaseState, T> action)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction(deferred: false);
        List<TItem> Read<TItem>(string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT Payload FROM {table}";
            using var reader = cmd.ExecuteReader();
            var result = new List<TItem>();
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<TItem>(reader.GetString(0))!);
            return result;
        }
        var state = new DatabaseState
        {
            Sessions = Read<ImageSession>(Tables[0]), Active = Read<ActiveSession>(Tables[1]),
            Generations = Read<ImageGeneration>(Tables[2]), Assets = Read<StoredAsset>(Tables[3]),
            Messages = Read<TelegramMessage>(Tables[4]), Jobs = Read<ImageJob>(Tables[5]),
            Usage = Read<UsageRecord>(Tables[6]), Drafts = Read<Draft>(Tables[7]), Updates = Read<HandledUpdate>(Tables[8]),
            AccessGrants = Read<AccessGrant>(Tables[9]), Prompts = Read<PromptReply>(Tables[10]), AccessRequests = Read<AccessRequest>(Tables[11]),
            Chats = Read<ChatConversation>(Tables[12]), ChatRequests = Read<ChatRequest>(Tables[13])
        };
        var result = action(state);
        void Write<TItem>(string table, IEnumerable<TItem> items, Func<TItem, string> key)
        {
            foreach (var item in items)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT INTO {table}(Id,Payload) VALUES($id,$payload) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload";
                cmd.Parameters.AddWithValue("$id", key(item));
                cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item));
                cmd.ExecuteNonQuery();
            }
        }
        Write(Tables[0], state.Sessions, x => x.Id.ToString()); Write(Tables[1], state.Active, x => x.Id);
        Write(Tables[2], state.Generations, x => x.Id.ToString()); Write(Tables[3], state.Assets, x => x.Id.ToString());
        Write(Tables[4], state.Messages, x => x.Id); Write(Tables[5], state.Jobs, x => x.Id.ToString());
        Write(Tables[6], state.Usage, x => x.Id.ToString()); Write(Tables[7], state.Drafts, x => x.Id);
        Write(Tables[8], state.Updates, x => x.Id);
        Write(Tables[9], state.AccessGrants, x => x.Id);
        Write(Tables[10], state.Prompts, x => x.Id);
        Write(Tables[11], state.AccessRequests, x => x.Id.ToString());
        Write(Tables[12], state.Chats, x => x.Id);
        Write(Tables[13], state.ChatRequests, x => x.Id);
        tx.Commit();
        return result;
    }
}

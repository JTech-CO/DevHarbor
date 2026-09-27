using Microsoft.Data.Sqlite;

namespace DevHarbor.Ledger;

internal sealed record LedgerOperation(string Id, string Action, string State, string Receipt, string Destination, string? ParentId, string TargetKey, string? Reason, string UpdatedAt);
internal sealed class SqliteLedger : IDisposable
{
    private readonly SqliteConnection connection;
    internal SqliteLedger(string path,bool initialize=false)
    {
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA journal_mode=WAL;"); Execute("PRAGMA synchronous=FULL;"); Execute("PRAGMA foreign_keys=ON;");
            Execute("""
                CREATE TABLE IF NOT EXISTS plans(id TEXT PRIMARY KEY, digest TEXT NOT NULL, payload TEXT NOT NULL, state TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY REFERENCES plans(id), action TEXT NOT NULL, state TEXT NOT NULL,
                    receipt TEXT NOT NULL, destination TEXT NOT NULL, parent_id TEXT, target_key TEXT NOT NULL, reason TEXT, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS reservations(target_key TEXT PRIMARY KEY, operation_id TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY, operation_id TEXT NOT NULL, state TEXT NOT NULL, at TEXT NOT NULL);
                """);
            if (Scalar("PRAGMA synchronous;") != "2" || Scalar("PRAGMA journal_mode;") != "wal" || Scalar("PRAGMA quick_check;") != "ok")
                throw new IOException("Ledger durability/integrity check failed");
        }
        catch { connection.Dispose(); throw; }
    }
    internal void Prepare(string id,string digest,string payload)
        => Execute("INSERT INTO plans VALUES($id,$digest,$payload,'Prepared');", ("$id",id),("$digest",digest),("$payload",payload));
    internal void Reject(string id,string reason,string at)
    {
        using var tx=connection.BeginTransaction();
        using var update=Command("UPDATE plans SET state='NotApplied' WHERE id=$id AND state='Prepared';",("$id",id));update.Transaction=tx;update.ExecuteNonQuery();
        using var log=Command("INSERT INTO events(operation_id,state,at) VALUES($id,$state,$at);",("$id",id),("$state","NotApplied:"+reason),("$at",at));log.Transaction=tx;log.ExecuteNonQuery();tx.Commit();
    }
    internal bool IsPrepared(string id,string digest,string payload)
        => Scalar("SELECT COUNT(*) FROM plans WHERE id=$id AND digest=$digest AND payload=$payload AND state='Prepared';",("$id",id),("$digest",digest),("$payload",payload)) == "1";
    internal void BeginIntent(string id,string digest,string payload,string action,string receipt,string destination,string? parentId,string targetKey,string at)
    {
        using var transaction = connection.BeginTransaction();
        using var check = Command("UPDATE plans SET state='Running' WHERE id=$id AND digest=$digest AND payload=$payload AND state='Prepared';",("$id",id),("$digest",digest),("$payload",payload)); check.Transaction=transaction;
        if (check.ExecuteNonQuery()!=1) throw new IOException("Plan is no longer prepared");
        using var claim = Command(parentId == null ? "INSERT INTO reservations VALUES($key,$id);" : "UPDATE reservations SET operation_id=operation_id WHERE target_key=$key AND operation_id=$id;",("$key",targetKey),("$id",parentId??id)); claim.Transaction=transaction;
        if (claim.ExecuteNonQuery()!=1) throw new IOException("Reservation unavailable");
        using var insert = Command("INSERT INTO operations VALUES($id,$action,'Intent',$receipt,$destination,$parent,$key,NULL,$at);",("$id",id),("$action",action),("$receipt",receipt),("$destination",destination),("$parent",parentId),("$key",targetKey),("$at",at)); insert.Transaction=transaction; insert.ExecuteNonQuery();
        using var log = Command("INSERT INTO events(operation_id,state,at) VALUES($id,'Intent',$at);",("$id",id),("$at",at));log.Transaction=transaction;log.ExecuteNonQuery();
        transaction.Commit();
    }
    internal void Finish(string id,string state,string? reason,string at)
    {
        using var transaction=connection.BeginTransaction();
        using var update=Command("UPDATE operations SET state=$state,reason=$reason,updated_at=$at WHERE id=$id;",("$state",state),("$reason",reason),("$at",at),("$id",id));update.Transaction=transaction;
        if(update.ExecuteNonQuery()!=1) throw new IOException("Operation missing");
        using var plan=Command("UPDATE plans SET state=$state WHERE id=$id;",("$state",state),("$id",id));plan.Transaction=transaction;plan.ExecuteNonQuery();
        if(state=="NotApplied")
        { using var release=Command("DELETE FROM reservations WHERE operation_id=$id;",("$id",id));release.Transaction=transaction;release.ExecuteNonQuery(); }
        if(state=="Restored")
        {
            using var parent=Command("UPDATE operations SET state='Restored',updated_at=$at WHERE id=(SELECT parent_id FROM operations WHERE id=$id);",("$id",id),("$at",at));parent.Transaction=transaction;parent.ExecuteNonQuery();
            using var parentPlan=Command("UPDATE plans SET state='Restored' WHERE id=(SELECT parent_id FROM operations WHERE id=$id);",("$id",id));parentPlan.Transaction=transaction;parentPlan.ExecuteNonQuery();
            using var release=Command("DELETE FROM reservations WHERE operation_id=(SELECT parent_id FROM operations WHERE id=$id);",("$id",id));release.Transaction=transaction;release.ExecuteNonQuery();
        }
        using var log=Command("INSERT INTO events(operation_id,state,at) VALUES($id,$state,$at);",("$id",id),("$state",state),("$at",at));log.Transaction=transaction;log.ExecuteNonQuery();
        transaction.Commit();
    }
    internal IReadOnlyList<LedgerOperation> Operations()
    {
        using var command=Command("SELECT id,action,state,receipt,destination,parent_id,target_key,reason,updated_at FROM operations ORDER BY rowid DESC;");
        using var reader=command.ExecuteReader();var result=new List<LedgerOperation>();
        while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.GetString(8)));
        return result;
    }
    private SqliteCommand Command(string sql,params (string Key,string? Value)[] values)
    {var command=connection.CreateCommand();command.CommandText=sql;foreach(var pair in values)command.Parameters.AddWithValue(pair.Key,(object?)pair.Value??DBNull.Value);return command;}
    private void Execute(string sql,params (string Key,string? Value)[] values) {using var command=Command(sql,values);command.ExecuteNonQuery();}
    private string? Scalar(string sql,params (string Key,string? Value)[] values) {using var command=Command(sql,values);return command.ExecuteScalar()?.ToString();}
    public void Dispose()=>connection.Dispose();
}

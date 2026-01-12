using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Data;

namespace CloudDemoNet8
{
    // Encapsulates all database logic
    public class SynteryRepository
    {
        private readonly string _connString;

        public SynteryRepository(string connString)
        {
            _connString = connString;
        }

        private IDbConnection CreateConnection() => new SqlConnection(_connString);

        // 1. Check if a user exists with specific face data
        public async Task<bool> HasFaceDataAsync(int enrollId)
        {
            using var conn = CreateConnection();
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM tblusers_face WHERE enrollid = @Id AND backupnum = 50",
                new { Id = enrollId });
            return count > 0;
        }

        // 2. Insert or Update a User (Upsert)
        public async Task UpsertUserAsync(int enrollId, string name, int backupNum, int isAdmin, string? record)
        {
            using var conn = CreateConnection();
            const string sql = @"
                MERGE tblusers_face AS target
                USING (SELECT @Id AS enrollid, @Num AS backupnum) AS source
                ON (target.enrollid = source.enrollid AND target.backupnum = source.backupnum)
                WHEN MATCHED THEN
                    UPDATE SET 
                        username = @Name, 
                        admin = @Admin, 
                        record = @Record, 
                        regdattime = GETDATE(), 
                        isactive = 1
                WHEN NOT MATCHED THEN
                    INSERT (enrollid, username, backupnum, admin, record, regdattime, isactive)
                    VALUES (@Id, @Name, @Num, @Admin, @Record, GETDATE(), 1);";

            await conn.ExecuteAsync(sql, new
            {
                Id = enrollId,
                Name = name ?? string.Empty,
                Num = backupNum,
                Admin = isAdmin,
                Record = record
            });
        }

        // 3. Log Attendance
        public async Task LogAttendanceAsync(int? enrollId, string deviceSn, DateTime time, double? distance)
        {
            using var conn = CreateConnection();
            const string sql = @"
                INSERT INTO tblattendance_face (enrollid, device, attendattime) 
                VALUES (@Id, @Sn, @Time)";

            await conn.ExecuteAsync(sql, new { Id = enrollId, Sn = deviceSn, Time = time });
        }

        // 4. Set User Active Status
        public async Task SetUserActiveAsync(int enrollId, bool isActive)
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE tblusers_face SET isactive = @Active WHERE enrollid = @Id",
                new { Active = isActive ? 1 : 0, Id = enrollId });
        }

        // 5. Delete User (FULL DELETE — OLD PROJECT PARITY)
        // OLD behavior removed ALL backupnums (face, fp, card, pwd, etc.)
        public async Task DeleteUserAsync(int enrollId)
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM tblusers_face WHERE enrollid = @Id",
                new { Id = enrollId });
        }


        // 6. Get Username (Helper)
        public string? GetUserName(int enrollId)
        {
            using var conn = CreateConnection();
            return conn.QueryFirstOrDefault<string>(
                "SELECT TOP 1 username FROM tblusers_face WHERE enrollid = @Id",
                new { Id = enrollId });
        }

        public static async Task<List<(int EnrollId, string UserName, int IsActive)>>
    SearchUsersByNameAsync(string connStr, string name)
        {
            var results = new List<(int, string, int)>();

            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(@"
        SELECT enrollid, username, isactive
        FROM tblusers_face
        WHERE username LIKE @name
        ORDER BY username
    ", conn);

            cmd.Parameters.AddWithValue("@name", $"%{name}%");

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2)
                ));
            }

            return results;
        }


    }
}
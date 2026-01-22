using Microsoft.Extensions.Logging;
using MySqlConnector;
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

        private IDbConnection CreateConnection() => new MySqlConnection(_connString);

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
            INSERT INTO tblusers_face
            (enrollid, username, backupnum, admin, record, regdattime, isactive)
            VALUES
            (@Id, @Name, @Num, @Admin, @Record, NOW(), 1)
            ON DUPLICATE KEY UPDATE
                username    = VALUES(username),
                admin       = VALUES(admin),
                record      = VALUES(record),
                regdattime  = NOW(),
                isactive    = 1;";

            await conn.ExecuteAsync(sql, new
            {
                Id = enrollId,
                Name = name ?? string.Empty,
                Num = backupNum,
                Admin = isAdmin,
                Record = record
            });
        }

        public async Task LogAttendanceAsync(int? enrollId, string deviceSn, DateTime time, double? distance)
        {
            using var conn = CreateConnection();

            // 1️ Get THIS USER'S last scan time only
            const string lastScanSql = @"
            SELECT attendattime
            FROM tblattendance_face
            WHERE enrollid = @EnrollId
            ORDER BY attendattime DESC
            LIMIT 1";

            var lastScanTime = await conn.ExecuteScalarAsync<DateTime?>(lastScanSql,new { EnrollId = enrollId });

            // 2️ Block double scan within 20 seconds
            if (lastScanTime.HasValue && (time - lastScanTime.Value).TotalSeconds < 20)
            {
                return; // same user scanned too fast → ignore
            }

            // 3️ Insert attendance
            const string insertSql = @"
        INSERT INTO tblattendance_face (enrollid, device, attendattime)
        VALUES (@EnrollId, @Device, @Time)";

            await conn.ExecuteAsync(insertSql, new
            {
                EnrollId = enrollId,
                Device = deviceSn,
                Time = time
            });

            // 4️ Get username using enrollId
            const string usernameSql = @"
            SELECT username
            FROM tblusers_face
            WHERE enrollid = @EnrollId";

            string? username_enroll = await conn.ExecuteScalarAsync<string?>(usernameSql,new { EnrollId = enrollId });

        }

        

        /*
        // 3. Log Attendance
        public async Task LogAttendanceAsync(int? enrollId, string deviceSn, DateTime time, double? distance)
        {
            using var conn = CreateConnection();
            const string sql = @"
                INSERT INTO tblattendance_face (enrollid, device, attendattime) 
                VALUES (@Id, @Sn, @Time)";

            await conn.ExecuteAsync(sql, new { Id = enrollId, Sn = deviceSn, Time = time });
        }
        */

        // 4. Set User Active Status
        public async Task SetUserActiveAsync(int enrollId, bool isActive)
        {
            using var conn = CreateConnection();
            await conn.ExecuteAsync(
                @"UPDATE tblusers_face 
                  SET isactive = @Active 
                  WHERE enrollid = @Id",
                new
                {
                    Active = isActive ? 1 : 0,
                    Id = enrollId
                });
        }

        // 5. Delete User (FULL DELETE )
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
                @"SELECT username 
                  FROM tblusers_face 
                  WHERE enrollid = @Id 
                  LIMIT 1",
                new { Id = enrollId });
        }

        public static async Task<List<(int EnrollId, string UserName, int IsActive)>>
            SearchUsersByNameAsync(string connStr, string name)
        {
            var results = new List<(int, string, int)>();

            using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            using var cmd = new MySqlCommand(@"
            SELECT enrollid, username, isactive
            FROM tblusers_face
            WHERE username LIKE @name
            ORDER BY username", conn);

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
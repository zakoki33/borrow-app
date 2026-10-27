using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP: เครื่องตัวเอง
    options.ListenLocalhost(5000);

    // HTTPS: รับการเชื่อมต่อจากเครือข่าย
    options.ListenAnyIP(5001, listenOptions =>
    {
        listenOptions.UseHttps();
    });
});
// ให้ชื่อ property ใน JSON เป็นตามที่เขียนไว้ (ID, Name, borrowerId ...) หน้าเว็บจะได้ใช้ได้เหมือนเดิม
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);
var app = builder.Build();

var cs = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ไม่พบ ConnectionStrings:Default ใน appsettings.json");

SqlConnection Db() => new(cs);

app.UseDefaultFiles();
app.UseStaticFiles();

// เปิด http://localhost:5000/api/ping เพื่อทดสอบว่าเชื่อมต่อฐานข้อมูลได้
app.MapGet("/api/ping", () =>
{
    try
    {
        using var db = Db();
        db.Open();
        return Results.Json(new { ok = true, server = db.DataSource, database = db.Database });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});

// หน้าเว็บเรียกทุกอย่างผ่านทางเดียว: POST /api/rpc  { action, args }
app.MapPost("/api/rpc", (RpcReq req) =>
{
    try
    {
        string A(int i) => req.Args is { Length: > 0 } a && i < a.Length
            ? (a[i].ValueKind == JsonValueKind.String ? a[i].GetString() ?? "" : a[i].ToString())
            : "";

        object? data = req.Action switch
        {
            "getEquipmentList" => GetEquipment(),
            "getBorrowers"     => GetBorrowers(),
            "getBorrowerById"  => GetBorrower(A(0)),
            "getHistoryList"   => GetHistory(),
            "borrowEquipment"  => Borrow(A(0), A(1), A(2)),
            "returnEquipment"  => Return(A(0)),
            _ => throw new InvalidOperationException("ไม่รู้จักคำสั่ง: " + req.Action)
        };
        return Results.Json(new { ok = true, data });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ค่าเริ่มต้นเปิดเฉพาะเครื่องนี้ ถ้าจะให้คนอื่นเปิดได้ ให้แก้ ListenUrl ใน appsettings.json เป็น http://0.0.0.0:5000
//var listenUrl = builder.Configuration["ListenUrl"]
    //?? "http://localhost:5000;http://localhost:5001";
app.Run();
//app.Run(listenUrl);

// ===================== ฟังก์ชันอ่าน/เขียนฐานข้อมูล =====================

List<EquipRow> GetEquipment()
{
    using var db = Db();
    return db.Query<EquipRow>("""
        SELECT PartId AS ID, Name, Type, Location, Status,
               BorrowerId AS borrowerId, BorrowerName AS currentBorrower,
               CONVERT(varchar(10), StartDate, 23) AS borrowDate,
               CONVERT(varchar(10), DueDate, 23)   AS dueDate
        FROM dbo.vPartStatus
        ORDER BY PartId
        """).ToList();
}

List<BorrowerRow> GetBorrowers()
{
    using var db = Db();
    return db.Query<BorrowerRow>("""
        SELECT PersonId AS ID, FullName AS Name, Department, Phone
        FROM dbo.Persons WHERE IsActive = 1 ORDER BY PersonId
        """).ToList();
}

BorrowerRow? GetBorrower(string id)
{
    using var db = Db();
    return db.QueryFirstOrDefault<BorrowerRow>("""
        SELECT PersonId AS ID, FullName AS Name, Department, Phone
        FROM dbo.Persons WHERE PersonId = @id AND IsActive = 1
        """, new { id = id.Trim() });
}

List<HistoryRow> GetHistory()
{
    using var db = Db();
    return db.Query<HistoryRow>("""
        SELECT 'T' + CAST(l.LoanId AS varchar(20)) AS TransactionID,
               l.PartId AS EquipmentID,
               l.PersonId AS BorrowerID,
               pe.FullName AS BorrowerName,
               CONVERT(varchar(10), l.StartDate, 23) AS BorrowDate,
               CONVERT(varchar(10), l.DueDate, 23)   AS DueDate,
               ISNULL(CONVERT(varchar(19), l.ReturnedAt, 120), '') AS ReturnDate,
               CASE WHEN l.ReturnedAt IS NULL THEN N'ยืมอยู่' ELSE N'คืนแล้ว' END AS Status
        FROM dbo.Loans l
        JOIN dbo.Persons pe ON pe.PersonId = l.PersonId
        ORDER BY l.LoanId DESC
        """).ToList();
}

// วันเริ่มยืม = วันนี้ (ตั้งโดยฐานข้อมูล) ผู้ยืมต้องมี ID ตรงทั้งค่า
Result Borrow(string partId, string personId, string due)
{
    partId = partId.Trim();
    if (partId == "") return new Result(false, "รหัสอุปกรณ์ไม่ถูกต้อง");
    if (!DateTime.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
        return new Result(false, "กรุณาระบุวันที่สิ้นสุด");
    if (dueDate.Date < DateTime.Today) return new Result(false, "วันที่สิ้นสุดต้องไม่ก่อนวันนี้");

    using var db = Db();
    var person = GetBorrower(personId);
    if (person is null) return new Result(false, "ไม่พบ ID ผู้ยืม");

    var canonicalPartId = db.QueryFirstOrDefault<string>(
        "SELECT PartId FROM dbo.Parts WHERE PartId = @partId AND IsActive = 1", new { partId });
    if (canonicalPartId is null) return new Result(false, "ไม่พบอุปกรณ์");

    try
    {
        db.Execute("""
            INSERT INTO dbo.Loans (PartId, PersonId, StartDate, DueDate)
            VALUES (@partId, @personId, CAST(GETDATE() AS DATE), @dueDate)
            """, new { partId = canonicalPartId, personId = person.ID, dueDate = dueDate.Date });
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
        return new Result(false, "อุปกรณ์นี้ถูกยืมไปแล้ว");
    }
    return new Result(true, $"ยืมอุปกรณ์เรียบร้อยแล้ว ({person.Name})");
}

Result Return(string partId)
{
    using var db = Db();
    var n = db.Execute("UPDATE dbo.Loans SET ReturnedAt = SYSDATETIME() WHERE PartId = @partId AND ReturnedAt IS NULL",
        new { partId = partId.Trim() });
    return n > 0 ? new Result(true, "คืนอุปกรณ์เรียบร้อยแล้ว")
                 : new Result(false, "อุปกรณ์ไม่ได้อยู่ในสถานะถูกยืม");
}

// ===================== ชนิดข้อมูล =====================
record RpcReq(string Action, JsonElement[]? Args);
record Result(bool success, string message);
record EquipRow(string ID, string Name, string? Type, string? Location, string Status,
                string? borrowerId, string? currentBorrower, string? borrowDate, string? dueDate);
record BorrowerRow(string ID, string Name, string? Department, string? Phone);
record HistoryRow(string TransactionID, string EquipmentID, string BorrowerID, string BorrowerName,
                  string BorrowDate, string DueDate, string ReturnDate, string Status);
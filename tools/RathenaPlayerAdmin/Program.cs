using System.Text.Json;
using Dapper;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<AdminRepository>();
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/characters", async (string? q, AdminRepository repo) =>
    Results.Ok(await repo.SearchCharactersAsync(q ?? string.Empty)));

app.MapGet("/api/characters/{charId:int}", async (int charId, AdminRepository repo) =>
{
    var character = await repo.GetCharacterAsync(charId);
    return character is null ? Results.NotFound() : Results.Ok(character);
});

app.MapGet("/api/characters/{charId:int}/inventory", async (int charId, AdminRepository repo) =>
    Results.Ok(await repo.GetItemsAsync("inventory", "char_id", charId)));

app.MapGet("/api/accounts/{accountId:int}/storage", async (int accountId, AdminRepository repo) =>
    Results.Ok(await repo.GetItemsAsync("storage", "account_id", accountId)));

app.MapPut("/api/characters/{charId:int}/stats", async (int charId, CharacterStats input, HttpContext http, AdminRepository repo) =>
{
    var result = await repo.UpdateStatsAsync(charId, input, GetOperator(http));
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPut("/api/items/{container}/{id:int}", async (string container, int id, ItemUpdate input, HttpContext http, AdminRepository repo) =>
{
    if (container is not ("inventory" or "storage"))
        return Results.BadRequest(new { error = "Unsupported container." });

    var result = await repo.UpdateItemAsync(container, id, input, GetOperator(http));
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapFallbackToFile("index.html");
app.Run();

static string GetOperator(HttpContext context) =>
    context.Request.Headers.TryGetValue("X-Admin-User", out var value) && !string.IsNullOrWhiteSpace(value)
        ? value.ToString()[..Math.Min(value.ToString().Length, 80)]
        : "local-admin";

sealed class AdminRepository(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Rathena")
        ?? throw new InvalidOperationException("ConnectionStrings:Rathena is required.");

    private MySqlConnection Open() => new(_connectionString);

    public async Task<IEnumerable<object>> SearchCharactersAsync(string query)
    {
        const string sql = """
            SELECT char_id AS CharId, account_id AS AccountId, name AS Name, class AS Class,
                   base_level AS BaseLevel, job_level AS JobLevel, zeny AS Zeny,
                   last_map AS LastMap, online AS Online
            FROM `char`
            WHERE (@q = '' OR name LIKE CONCAT('%', @q, '%') OR char_id = @id OR account_id = @id)
            ORDER BY online DESC, name
            LIMIT 100;
            """;
        _ = int.TryParse(query, out var id);
        await using var db = Open();
        return await db.QueryAsync(sql, new { q = query, id });
    }

    public async Task<object?> GetCharacterAsync(int charId)
    {
        const string sql = """
            SELECT char_id AS CharId, account_id AS AccountId, name AS Name, class AS Class,
                   base_level AS BaseLevel, job_level AS JobLevel, base_exp AS BaseExp, job_exp AS JobExp,
                   zeny AS Zeny, `str` AS Str, agi AS Agi, vit AS Vit, `int` AS IntStat,
                   dex AS Dex, luk AS Luk, pow AS Pow, sta AS Sta, wis AS Wis, spl AS Spl,
                   con AS Con, crt AS Crt, max_hp AS MaxHp, hp AS Hp, max_sp AS MaxSp, sp AS Sp,
                   max_ap AS MaxAp, ap AS Ap, status_point AS StatusPoint, skill_point AS SkillPoint,
                   trait_point AS TraitPoint, last_map AS LastMap, last_x AS LastX, last_y AS LastY,
                   online AS Online
            FROM `char` WHERE char_id = @charId;
            """;
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync(sql, new { charId });
    }

    public async Task<IEnumerable<object>> GetItemsAsync(string table, string ownerColumn, int ownerId)
    {
        var sql = $"""
            SELECT id AS Id, {ownerColumn} AS OwnerId, nameid AS NameId, amount AS Amount,
                   equip AS Equip, identify AS Identify, refine AS Refine, attribute AS Attribute,
                   card0 AS Card0, card1 AS Card1, card2 AS Card2, card3 AS Card3,
                   option_id0 AS OptionId0, option_val0 AS OptionVal0, option_parm0 AS OptionParm0,
                   option_id1 AS OptionId1, option_val1 AS OptionVal1, option_parm1 AS OptionParm1,
                   option_id2 AS OptionId2, option_val2 AS OptionVal2, option_parm2 AS OptionParm2,
                   option_id3 AS OptionId3, option_val3 AS OptionVal3, option_parm3 AS OptionParm3,
                   option_id4 AS OptionId4, option_val4 AS OptionVal4, option_parm4 AS OptionParm4,
                   expire_time AS ExpireTime, bound AS Bound, unique_id AS UniqueId,
                   enchantgrade AS EnchantGrade
            FROM `{table}` WHERE `{ownerColumn}` = @ownerId ORDER BY equip DESC, id;
            """;
        await using var db = Open();
        return await db.QueryAsync(sql, new { ownerId });
    }

    public async Task<OperationResult> UpdateStatsAsync(int charId, CharacterStats input, string admin)
    {
        if (!input.IsValid(out var error)) return OperationResult.Fail(error);
        await using var db = Open();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        var online = await db.ExecuteScalarAsync<int>("SELECT online FROM `char` WHERE char_id=@charId FOR UPDATE", new { charId }, tx);
        if (online != 0) return OperationResult.Fail("角色在線中，為避免回存覆蓋，請先讓角色登出。");

        const string sql = """
            UPDATE `char` SET
              `str`=@Str, agi=@Agi, vit=@Vit, `int`=@IntStat, dex=@Dex, luk=@Luk,
              pow=@Pow, sta=@Sta, wis=@Wis, spl=@Spl, con=@Con, crt=@Crt,
              base_level=@BaseLevel, job_level=@JobLevel, zeny=@Zeny,
              status_point=@StatusPoint, skill_point=@SkillPoint, trait_point=@TraitPoint
            WHERE char_id=@charId;
            """;
        var affected = await db.ExecuteAsync(sql, new
        {
            charId, input.Str, input.Agi, input.Vit, input.IntStat, input.Dex, input.Luk,
            input.Pow, input.Sta, input.Wis, input.Spl, input.Con, input.Crt,
            input.BaseLevel, input.JobLevel, input.Zeny, input.StatusPoint, input.SkillPoint, input.TraitPoint
        }, tx);
        if (affected == 0) return OperationResult.Fail("找不到角色。");

        await WriteAuditAsync(db, tx, admin, "character.stats.update", "char", charId, input);
        await tx.CommitAsync();
        return OperationResult.Ok();
    }

    public async Task<OperationResult> UpdateItemAsync(string table, int id, ItemUpdate input, string admin)
    {
        if (!input.IsValid(out var error)) return OperationResult.Fail(error);
        var ownerColumn = table == "inventory" ? "char_id" : "account_id";
        await using var db = Open();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        var ownerId = await db.ExecuteScalarAsync<int?>($"SELECT `{ownerColumn}` FROM `{table}` WHERE id=@id FOR UPDATE", new { id }, tx);
        if (ownerId is null) return OperationResult.Fail("找不到物品。");
        if (table == "inventory")
        {
            var online = await db.ExecuteScalarAsync<int>("SELECT online FROM `char` WHERE char_id=@ownerId", new { ownerId }, tx);
            if (online != 0) return OperationResult.Fail("角色在線中，請先登出再修改背包或裝備。");
        }

        var sql = $"""
            UPDATE `{table}` SET nameid=@NameId, amount=@Amount, equip=@Equip, identify=@Identify,
              refine=@Refine, attribute=@Attribute, card0=@Card0, card1=@Card1, card2=@Card2, card3=@Card3,
              option_id0=@OptionId0, option_val0=@OptionVal0, option_parm0=@OptionParm0,
              option_id1=@OptionId1, option_val1=@OptionVal1, option_parm1=@OptionParm1,
              option_id2=@OptionId2, option_val2=@OptionVal2, option_parm2=@OptionParm2,
              option_id3=@OptionId3, option_val3=@OptionVal3, option_parm3=@OptionParm3,
              option_id4=@OptionId4, option_val4=@OptionVal4, option_parm4=@OptionParm4,
              expire_time=@ExpireTime, bound=@Bound, enchantgrade=@EnchantGrade
            WHERE id=@id;
            """;
        await db.ExecuteAsync(sql, new
        {
            id, input.NameId, input.Amount, input.Equip, input.Identify, input.Refine, input.Attribute,
            input.Card0, input.Card1, input.Card2, input.Card3,
            input.OptionId0, input.OptionVal0, input.OptionParm0,
            input.OptionId1, input.OptionVal1, input.OptionParm1,
            input.OptionId2, input.OptionVal2, input.OptionParm2,
            input.OptionId3, input.OptionVal3, input.OptionParm3,
            input.OptionId4, input.OptionVal4, input.OptionParm4,
            input.ExpireTime, input.Bound, input.EnchantGrade
        }, tx);
        await WriteAuditAsync(db, tx, admin, $"{table}.update", table, id, input);
        await tx.CommitAsync();
        return OperationResult.Ok();
    }

    private static async Task WriteAuditAsync(MySqlConnection db, MySqlTransaction tx, string admin, string action, string targetType, int targetId, object payload)
    {
        const string create = """
            CREATE TABLE IF NOT EXISTS `dotnet_admin_audit` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
              `admin_user` VARCHAR(80) NOT NULL,
              `action` VARCHAR(80) NOT NULL,
              `target_type` VARCHAR(40) NOT NULL,
              `target_id` BIGINT NOT NULL,
              `payload_json` JSON NOT NULL,
              PRIMARY KEY (`id`), KEY `target` (`target_type`,`target_id`)
            ) ENGINE=InnoDB;
            """;
        await db.ExecuteAsync(create, transaction: tx);
        await db.ExecuteAsync("INSERT INTO dotnet_admin_audit(admin_user,action,target_type,target_id,payload_json) VALUES(@admin,@action,@targetType,@targetId,@payload)",
            new { admin, action, targetType, targetId, payload = JsonSerializer.Serialize(payload) }, tx);
    }
}

record CharacterStats(int Str, int Agi, int Vit, int IntStat, int Dex, int Luk,
    int Pow, int Sta, int Wis, int Spl, int Con, int Crt,
    int BaseLevel, int JobLevel, long Zeny, int StatusPoint, int SkillPoint, int TraitPoint)
{
    public bool IsValid(out string error)
    {
        var stats = new[] { Str, Agi, Vit, IntStat, Dex, Luk, Pow, Sta, Wis, Spl, Con, Crt };
        if (stats.Any(x => x is < 0 or > 10000)) { error = "素質必須介於 0 至 10000。"; return false; }
        if (BaseLevel is < 1 or > 1000 || JobLevel is < 1 or > 1000) { error = "等級超出允許範圍。"; return false; }
        if (Zeny is < 0 or > uint.MaxValue) { error = "Zeny 超出資料庫範圍。"; return false; }
        if (StatusPoint < 0 || SkillPoint < 0 || TraitPoint < 0) { error = "點數不可為負數。"; return false; }
        error = string.Empty; return true;
    }
}

record ItemUpdate(int NameId, int Amount, uint Equip, int Identify, int Refine, int Attribute,
    uint Card0, uint Card1, uint Card2, uint Card3,
    int OptionId0, int OptionVal0, int OptionParm0, int OptionId1, int OptionVal1, int OptionParm1,
    int OptionId2, int OptionVal2, int OptionParm2, int OptionId3, int OptionVal3, int OptionParm3,
    int OptionId4, int OptionVal4, int OptionParm4, uint ExpireTime, int Bound, int EnchantGrade)
{
    public bool IsValid(out string error)
    {
        if (NameId <= 0) { error = "NameId 必須大於 0。"; return false; }
        if (Amount is < 0 or > 30000) { error = "數量必須介於 0 至 30000。"; return false; }
        if (Refine is < 0 or > 100 || EnchantGrade is < 0 or > 100) { error = "精煉或附魔等級超出範圍。"; return false; }
        error = string.Empty; return true;
    }
}

record OperationResult(bool Success, string? Error)
{
    public static OperationResult Ok() => new(true, null);
    public static OperationResult Fail(string error) => new(false, error);
}

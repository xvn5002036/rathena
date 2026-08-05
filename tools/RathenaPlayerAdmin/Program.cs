using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<AdminRepository>();
builder.Services.AddSingleton<KickService>();
builder.Services.AddSingleton(new AtCommandCatalog(Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "doc", "atcommands.txt"))));
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

app.MapPost("/api/characters/{charId:int}/inventory", async (int charId, ItemUpdate input, HttpContext http, AdminRepository repo) =>
{
    var result = await repo.CreateItemAsync("inventory", charId, input, GetOperator(http));
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/accounts/{accountId:int}/storage", async (int accountId, ItemUpdate input, HttpContext http, AdminRepository repo) =>
{
    var result = await repo.CreateItemAsync("storage", accountId, input, GetOperator(http));
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapDelete("/api/items/{container}/{id:int}", async (string container, int id, HttpContext http, AdminRepository repo) =>
{
    if (container is not ("inventory" or "storage"))
        return Results.BadRequest(new { error = "不支援的物品容器。" });
    var result = await repo.DeleteItemAsync(container, id, GetOperator(http));
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/characters/{sourceCharId:int}/clone-to-gm", async (int sourceCharId, CloneCharacterRequest input, HttpContext http, AdminRepository repo) =>
{
    var result = await repo.CloneCharacterToGmAsync(sourceCharId, input.TargetCharId, GetOperator(http));
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/atcommands", (AtCommandCatalog catalog) => Results.Ok(catalog.Commands));

app.MapPost("/api/atcommands/execute", async (AtCommandRequest input, HttpContext http, AdminRepository repo, AtCommandCatalog catalog) =>
{
    var result = await repo.QueueAtCommandAsync(input.ExecutorCharId, input.Command, GetOperator(http), catalog);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/atcommands/status/{charId:int}", async (int charId, AdminRepository repo) =>
    Results.Ok(await repo.GetLastAtCommandStatusAsync(charId)));

app.MapFallbackToFile("index.html");
app.Run();

static string GetOperator(HttpContext context) =>
    context.Request.Headers.TryGetValue("X-Admin-User", out var value) && !string.IsNullOrWhiteSpace(value)
        ? value.ToString()[..Math.Min(value.ToString().Length, 80)]
        : "local-admin";

sealed class AdminRepository(IConfiguration configuration, KickService kickService)
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
            SELECT c.char_id AS CharId, c.account_id AS AccountId, c.name AS Name, c.class AS Class,
                   base_level AS BaseLevel, job_level AS JobLevel, base_exp AS BaseExp, job_exp AS JobExp,
                   zeny AS Zeny, `str` AS Str, agi AS Agi, vit AS Vit, `int` AS IntStat,
                   dex AS Dex, luk AS Luk, pow AS Pow, sta AS Sta, wis AS Wis, spl AS Spl,
                   con AS Con, crt AS Crt, max_hp AS MaxHp, hp AS Hp, max_sp AS MaxSp, sp AS Sp,
                   max_ap AS MaxAp, ap AS Ap, status_point AS StatusPoint, skill_point AS SkillPoint,
                   trait_point AS TraitPoint, last_map AS LastMap, last_x AS LastX, last_y AS LastY,
                   online AS Online,
                   COALESCE((SELECT value FROM acc_reg_num WHERE account_id=c.account_id AND `key`='#CASHPOINTS' AND `index`=0),0) AS CashPoints,
                   COALESCE((SELECT value FROM acc_reg_num WHERE account_id=c.account_id AND `key`='#KAFRAPOINTS' AND `index`=0),0) AS KafraPoints
            FROM `char` c WHERE c.char_id = @charId;
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
        var offlineError = await EnsureCharacterOfflineAsync(charId);
        if (offlineError is not null) return OperationResult.Fail(offlineError);
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

        var accountId = await db.ExecuteScalarAsync<int>("SELECT account_id FROM `char` WHERE char_id=@charId", new { charId }, tx);
        const string saveAccountPoint = "INSERT INTO acc_reg_num(account_id,`key`,`index`,`value`) VALUES(@accountId,@key,0,@value) ON DUPLICATE KEY UPDATE `value`=VALUES(`value`);";
        await db.ExecuteAsync(saveAccountPoint, new { accountId, key = "#CASHPOINTS", value = input.CashPoints }, tx);
        await db.ExecuteAsync(saveAccountPoint, new { accountId, key = "#KAFRAPOINTS", value = input.KafraPoints }, tx);

        await WriteAuditAsync(db, tx, admin, "character.stats.update", "char", charId, input);
        await tx.CommitAsync();
        return OperationResult.Ok();
    }

    public async Task<OperationResult> UpdateItemAsync(string table, int id, ItemUpdate input, string admin)
    {
        if (!input.IsValid(out var error)) return OperationResult.Fail(error);
        var ownerColumn = table == "inventory" ? "char_id" : "account_id";
        await using (var lookup = Open())
        {
            var owner = await lookup.ExecuteScalarAsync<int?>($"SELECT `{ownerColumn}` FROM `{table}` WHERE id=@id", new { id });
            if (owner is null) return OperationResult.Fail("找不到物品。");
            var offlineError = table == "inventory" ? await EnsureCharacterOfflineAsync(owner.Value) : await EnsureAccountOfflineAsync(owner.Value);
            if (offlineError is not null) return OperationResult.Fail(offlineError);
        }
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

    public async Task<OperationResult> CreateItemAsync(string table, int ownerId, ItemUpdate input, string admin)
    {
        if (table is not ("inventory" or "storage")) return OperationResult.Fail("不支援的物品容器。");
        if (!input.IsValid(out var error)) return OperationResult.Fail(error);
        var offlineError = table == "inventory" ? await EnsureCharacterOfflineAsync(ownerId) : await EnsureAccountOfflineAsync(ownerId);
        if (offlineError is not null) return OperationResult.Fail(offlineError);
        var ownerColumn = table == "inventory" ? "char_id" : "account_id";
        await using var db = Open();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        if (!await IsOwnerOfflineAsync(db, tx, table, ownerId)) return OperationResult.Fail("角色或帳號仍在線，請先登出再新增物品。");

        var sql = $"""
            INSERT INTO `{table}` (`{ownerColumn}`,nameid,amount,equip,identify,refine,attribute,
              card0,card1,card2,card3,option_id0,option_val0,option_parm0,option_id1,option_val1,option_parm1,
              option_id2,option_val2,option_parm2,option_id3,option_val3,option_parm3,option_id4,option_val4,option_parm4,
              expire_time,bound,enchantgrade)
            VALUES (@ownerId,@NameId,@Amount,@Equip,@Identify,@Refine,@Attribute,
              @Card0,@Card1,@Card2,@Card3,@OptionId0,@OptionVal0,@OptionParm0,@OptionId1,@OptionVal1,@OptionParm1,
              @OptionId2,@OptionVal2,@OptionParm2,@OptionId3,@OptionVal3,@OptionParm3,@OptionId4,@OptionVal4,@OptionParm4,
              @ExpireTime,@Bound,@EnchantGrade);
            """;
        var values = ItemParameters(input, ownerId);
        await db.ExecuteAsync(sql, values, tx);
        await WriteAuditAsync(db, tx, admin, $"{table}.create", table, ownerId, input);
        await tx.CommitAsync();
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteItemAsync(string table, int id, string admin)
    {
        var ownerColumn = table == "inventory" ? "char_id" : "account_id";
        await using (var lookup = Open())
        {
            var owner = await lookup.ExecuteScalarAsync<int?>($"SELECT `{ownerColumn}` FROM `{table}` WHERE id=@id", new { id });
            if (owner is null) return OperationResult.Fail("找不到物品。");
            var offlineError = table == "inventory" ? await EnsureCharacterOfflineAsync(owner.Value) : await EnsureAccountOfflineAsync(owner.Value);
            if (offlineError is not null) return OperationResult.Fail(offlineError);
        }
        await using var db = Open();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        var ownerId = await db.ExecuteScalarAsync<int?>($"SELECT `{ownerColumn}` FROM `{table}` WHERE id=@id FOR UPDATE", new { id }, tx);
        if (ownerId is null) return OperationResult.Fail("找不到物品。");
        if (!await IsOwnerOfflineAsync(db, tx, table, ownerId.Value)) return OperationResult.Fail("角色或帳號仍在線，請先登出再刪除物品。");
        await db.ExecuteAsync($"DELETE FROM `{table}` WHERE id=@id", new { id }, tx);
        await WriteAuditAsync(db, tx, admin, $"{table}.delete", table, id, new { ownerId });
        await tx.CommitAsync();
        return OperationResult.Ok();
    }

    public async Task<OperationResult> CloneCharacterToGmAsync(int sourceCharId, int targetCharId, string admin)
    {
        if (sourceCharId == targetCharId) return OperationResult.Fail("來源與目標角色不可相同。");
        var sourceOfflineError = await EnsureCharacterOfflineAsync(sourceCharId);
        if (sourceOfflineError is not null) return OperationResult.Fail(sourceOfflineError);
        var targetOfflineError = await EnsureCharacterOfflineAsync(targetCharId);
        if (targetOfflineError is not null) return OperationResult.Fail(targetOfflineError);
        await using var db = Open();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        var source = await db.QuerySingleOrDefaultAsync<(int AccountId, int Online)>("SELECT account_id AccountId, online Online FROM `char` WHERE char_id=@sourceCharId FOR UPDATE", new { sourceCharId }, tx);
        var target = await db.QuerySingleOrDefaultAsync<(int AccountId, int Online, int GroupId)>("SELECT c.account_id AccountId,c.online Online,l.group_id GroupId FROM `char` c JOIN login l ON l.account_id=c.account_id WHERE c.char_id=@targetCharId FOR UPDATE", new { targetCharId }, tx);
        if (source.AccountId == 0) return OperationResult.Fail("找不到來源角色。");
        if (target.AccountId == 0) return OperationResult.Fail("找不到目標 GM 角色。");
        if (target.GroupId <= 0) return OperationResult.Fail("目標角色的帳號不是 GM 帳號（group_id 必須大於 0）。");
        if (source.Online != 0 || target.Online != 0) return OperationResult.Fail("來源與目標 GM 都必須先登出。");

        const string copyStats = """
            UPDATE `char` target JOIN `char` source ON source.char_id=@sourceCharId SET
              target.class=source.class,target.base_level=source.base_level,target.job_level=source.job_level,
              target.base_exp=source.base_exp,target.job_exp=source.job_exp,target.zeny=source.zeny,
              target.`str`=source.`str`,target.agi=source.agi,target.vit=source.vit,target.`int`=source.`int`,
              target.dex=source.dex,target.luk=source.luk,target.pow=source.pow,target.sta=source.sta,
              target.wis=source.wis,target.spl=source.spl,target.con=source.con,target.crt=source.crt,
              target.max_hp=source.max_hp,target.hp=source.hp,target.max_sp=source.max_sp,target.sp=source.sp,
              target.max_ap=source.max_ap,target.ap=source.ap,target.status_point=source.status_point,
              target.skill_point=source.skill_point,target.trait_point=source.trait_point
            WHERE target.char_id=@targetCharId;
            """;
        await db.ExecuteAsync(copyStats, new { sourceCharId, targetCharId }, tx);
        await db.ExecuteAsync("DELETE FROM skill WHERE char_id=@targetCharId", new { targetCharId }, tx);
        await db.ExecuteAsync("INSERT INTO skill(char_id,id,lv,flag) SELECT @targetCharId,id,lv,flag FROM skill WHERE char_id=@sourceCharId", new { sourceCharId, targetCharId }, tx);
        await CloneContainerAsync(db, tx, "inventory", "char_id", sourceCharId, targetCharId);
        if (source.AccountId != target.AccountId)
            await CloneContainerAsync(db, tx, "storage", "account_id", source.AccountId, target.AccountId);

        await WriteAuditAsync(db, tx, admin, "character.clone_to_gm", "char", targetCharId,
            new { sourceCharId, targetCharId, sourceAccountId = source.AccountId, targetAccountId = target.AccountId });
        await tx.CommitAsync();
        return OperationResult.Ok();
    }

    public async Task<OperationResult> QueueAtCommandAsync(int executorCharId, string command, string admin, AtCommandCatalog catalog)
    {
        command = command.Trim();
        if (!catalog.IsAllowed(command)) return OperationResult.Fail("未知或不允許的 GM 指令。");
        if (command.Length > 500) return OperationResult.Fail("指令內容過長。");
        await using var db = Open();
        await db.OpenAsync();
        var gm = await db.QuerySingleOrDefaultAsync<(int AccountId, int Online, int GroupId)>("SELECT c.account_id AccountId,c.online Online,l.group_id GroupId FROM `char` c JOIN login l ON l.account_id=c.account_id WHERE c.char_id=@executorCharId", new { executorCharId });
        if (gm.AccountId == 0) return OperationResult.Fail("找不到執行指令的角色。");
        if (gm.GroupId <= 0) return OperationResult.Fail("選擇的角色不是 GM 帳號。");
        if (gm.Online == 0) return OperationResult.Fail("GM 角色必須在線，指令結果才會顯示在遊戲內。");
        const string create = """
            CREATE TABLE IF NOT EXISTS `dotnet_admin_atcommand_queue` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, `account_id` INT UNSIGNED NOT NULL,
              `char_id` INT UNSIGNED NOT NULL, `command` VARCHAR(500) NOT NULL,
              `requested_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, `processed_at` DATETIME NULL,
              PRIMARY KEY (`id`), KEY `pending` (`processed_at`,`id`)
            ) ENGINE=InnoDB;
            """;
        await db.ExecuteAsync(create);
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("INSERT INTO dotnet_admin_atcommand_queue(account_id,char_id,command) VALUES(@accountId,@executorCharId,@command)", new { accountId = gm.AccountId, executorCharId, command }, tx);
        await WriteAuditAsync(db, tx, admin, "atcommand.execute", "char", executorCharId, new { command });
        await tx.CommitAsync();
        return OperationResult.Ok();
    }

    public async Task<object?> GetLastAtCommandStatusAsync(int charId)
    {
        await using var db = Open();
        return await db.QuerySingleOrDefaultAsync("SELECT id AS Id,command AS Command,requested_at AS RequestedAt,processed_at AS ProcessedAt FROM dotnet_admin_atcommand_queue WHERE char_id=@charId ORDER BY id DESC LIMIT 1", new { charId });
    }

    private static async Task CloneContainerAsync(MySqlConnection db, MySqlTransaction tx, string table, string ownerColumn, int sourceOwnerId, int targetOwnerId)
    {
        var columns = (await db.QueryAsync<string>("SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME NOT IN ('id',@ownerColumn,'unique_id') ORDER BY ORDINAL_POSITION", new { table, ownerColumn }, tx)).ToArray();
        if (columns.Length == 0) throw new InvalidOperationException($"找不到 {table} 資料表欄位。");
        var quoted = string.Join(',', columns.Select(column => $"`{column.Replace("`", "``")}`"));
        await db.ExecuteAsync($"DELETE FROM `{table}` WHERE `{ownerColumn}`=@targetOwnerId", new { targetOwnerId }, tx);
        await db.ExecuteAsync($"INSERT INTO `{table}` (`{ownerColumn}`,{quoted}) SELECT @targetOwnerId,{quoted} FROM `{table}` WHERE `{ownerColumn}`=@sourceOwnerId", new { sourceOwnerId, targetOwnerId }, tx);
    }

    private static async Task<bool> IsOwnerOfflineAsync(MySqlConnection db, MySqlTransaction tx, string table, int ownerId) =>
        table == "inventory"
            ? await db.ExecuteScalarAsync<int?>("SELECT online FROM `char` WHERE char_id=@ownerId", new { ownerId }, tx) == 0
            : await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM `char` WHERE account_id=@ownerId AND online<>0", new { ownerId }, tx) == 0;

    private async Task<string?> EnsureCharacterOfflineAsync(int charId)
    {
        await using var db = Open();
        var online = await db.ExecuteScalarAsync<int?>("SELECT online FROM `char` WHERE char_id=@charId", new { charId });
        if (online is null) return "找不到角色。";
        if (online == 0) return null;
        if (!await kickService.KickAsync(charId)) return "無法建立踢人佇列，資料尚未修改。";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(250);
            if (await db.ExecuteScalarAsync<int>("SELECT online FROM `char` WHERE char_id=@charId", new { charId }) == 0) return null;
        }
        return "已送出踢人請求，但角色未在時間內離線。請確認隱藏 NPC dotnet_admin_kick 已載入。";
    }

    private async Task<string?> EnsureAccountOfflineAsync(int accountId)
    {
        await using var db = Open();
        var onlineCharacters = (await db.QueryAsync<int>("SELECT char_id FROM `char` WHERE account_id=@accountId AND online<>0", new { accountId })).ToArray();
        foreach (var charId in onlineCharacters)
        {
            var error = await EnsureCharacterOfflineAsync(charId);
            if (error is not null) return error;
        }
        return null;
    }

    private static object ItemParameters(ItemUpdate input, int ownerId) => new
    {
        ownerId, input.NameId, input.Amount, input.Equip, input.Identify, input.Refine, input.Attribute,
        input.Card0, input.Card1, input.Card2, input.Card3,
        input.OptionId0, input.OptionVal0, input.OptionParm0, input.OptionId1, input.OptionVal1, input.OptionParm1,
        input.OptionId2, input.OptionVal2, input.OptionParm2, input.OptionId3, input.OptionVal3, input.OptionParm3,
        input.OptionId4, input.OptionVal4, input.OptionParm4, input.ExpireTime, input.Bound, input.EnchantGrade
    };

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

sealed class KickService(IConfiguration configuration)
{
    public async Task<bool> KickAsync(int charId)
    {
        var connectionString = configuration.GetConnectionString("Rathena");
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        await using var db = new MySqlConnection(connectionString);
        const string create = """
            CREATE TABLE IF NOT EXISTS `dotnet_admin_kick_queue` (
              `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
              `char_id` INT UNSIGNED NOT NULL,
              `requested_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
              `processed_at` DATETIME NULL,
              PRIMARY KEY (`id`), KEY `pending` (`processed_at`,`id`)
            ) ENGINE=InnoDB;
            """;
        await db.ExecuteAsync(create);
        return await db.ExecuteAsync("INSERT INTO dotnet_admin_kick_queue(char_id) VALUES(@charId)", new { charId }) == 1;
    }
}

sealed class AtCommandCatalog
{
    private readonly HashSet<string> _names;
    public IReadOnlyList<AtCommandDefinition> Commands { get; }

    public AtCommandCatalog(string path)
    {
        var commands = new List<AtCommandDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDescription = new List<int>();
        var category = "其他指令";
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            var categoryMatch = Regex.Match(line, @"^\|\s*\d+\.\s*(.+?)\s*\|$");
            if (categoryMatch.Success) { category = categoryMatch.Groups[1].Value; pendingDescription.Clear(); continue; }
            var commandMatch = Regex.Match(line, @"^(@[A-Za-z][A-Za-z0-9_]*)\s*(.*)$");
            if (commandMatch.Success)
            {
                var name = commandMatch.Groups[1].Value.ToLowerInvariant();
                if (!seen.Add(name)) continue;
                commands.Add(new AtCommandDefinition(category, name, commandMatch.Groups[2].Value.Trim(), string.Empty, string.Empty));
                pendingDescription.Add(commands.Count - 1);
                continue;
            }
            if (pendingDescription.Count > 0 && line.Length > 0 && !line.StartsWith("-") && !line.StartsWith("=") && !line.StartsWith("Output Example", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var index in pendingDescription) commands[index] = commands[index] with { Description = line };
                pendingDescription.Clear();
            }
        }
        ApplyChineseTranslations(commands, path);
        Commands = commands;
        _names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyChineseTranslations(List<AtCommandDefinition> commands, string sourcePath)
    {
        var cachePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "atcommands.zh-TW.json");
        Dictionary<string, string> cache;
        try { cache = File.Exists(cachePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cachePath)) ?? new() : new(); }
        catch { cache = new(); }
        var missing = commands.Select(command => command.Description).Where(description => description.Length > 0 && !cache.ContainsKey(description)).Distinct().ToArray();
        if (missing.Length > 0)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var chunks = missing.Chunk(20).ToArray();
                var tasks = chunks.Select(async chunk =>
                {
                    var text = string.Join('\n', chunk);
                    var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-TW&dt=t&q=" + Uri.EscapeDataString(text);
                    using var document = JsonDocument.Parse(await client.GetStringAsync(url));
                    var translated = string.Concat(document.RootElement[0].EnumerateArray().Select(segment => segment[0].GetString()))
                        .Split('\n', StringSplitOptions.None);
                    return (Source: chunk, Translated: translated);
                }).ToArray();
                foreach (var result in Task.WhenAll(tasks).GetAwaiter().GetResult())
                    for (var index = 0; index < result.Source.Length && index < result.Translated.Length; index++)
                        cache[result.Source[index]] = result.Translated[index].Trim();
                File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* Keep the original description if translation is temporarily unavailable. */ }
        }
        for (var index = 0; index < commands.Count; index++)
            if (cache.TryGetValue(commands[index].Description, out var chinese)) commands[index] = commands[index] with { DescriptionZh = chinese };
    }

    public bool IsAllowed(string command)
    {
        var name = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return name is not null && _names.Contains(name);
    }
}

record AtCommandDefinition(string Category, string Name, string Usage, string Description, string DescriptionZh);
record AtCommandRequest(int ExecutorCharId, string Command);

record CharacterStats(int Str, int Agi, int Vit, int IntStat, int Dex, int Luk,
    int Pow, int Sta, int Wis, int Spl, int Con, int Crt,
    int BaseLevel, int JobLevel, long Zeny, int StatusPoint, int SkillPoint, int TraitPoint,
    uint CashPoints, uint KafraPoints)
{
    public bool IsValid(out string error)
    {
        var stats = new[] { Str, Agi, Vit, IntStat, Dex, Luk, Pow, Sta, Wis, Spl, Con, Crt };
        if (stats.Any(x => x is < 0 or > 10000)) { error = "素質必須介於 0 至 10000。"; return false; }
        if (BaseLevel is < 1 or > 1000 || JobLevel is < 1 or > 1000) { error = "等級超出允許範圍。"; return false; }
        if (Zeny is < 0 or > uint.MaxValue) { error = "Zeny 超出資料庫範圍。"; return false; }
        if (CashPoints > int.MaxValue || KafraPoints > int.MaxValue) { error = "商城點數不可超過 2,147,483,647。"; return false; }
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

record CloneCharacterRequest(int TargetCharId);

record OperationResult(bool Success, string? Error)
{
    public static OperationResult Ok() => new(true, null);
    public static OperationResult Fail(string error) => new(false, error);
}

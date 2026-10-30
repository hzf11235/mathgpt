using System.Text.Json;

namespace MathGPT.Core;

public sealed class StateStore
{
    public string Root { get; }
    private readonly string path;
    private readonly object gate = new();
    public AppState State { get; }
    public SecretVault Vault { get; }
    public StateStore(string root)
    {
        Root = SafeFiles.DirectoryPath(root);
        Directory.CreateDirectory(Root);
        path = SafeFiles.Child(root, "settings.json");
        Vault = new SecretVault(root);
        if (File.Exists(path))
        {
            try
            {
                State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonData.Options)
                    ?? throw new JsonException();
                if (State.SchemaVersion is not 1 and not 2) throw new UserException("这是较新版本的数据，请使用新版 MathGPT 打开。");
                State.SchemaVersion = 2;
            }
            catch (JsonException) { throw new UserException("设置文件损坏，已保留原文件。请从 settings.json.previous 恢复，或联系客服。"); }
        }
        else State = new AppState();
    }
    public void Save()
    {
        lock (gate)
        {
            var json = JsonSerializer.Serialize(State, JsonData.Options);
            if (File.Exists(path))
            {
                SafeFiles.NoLinks(path + ".previous");
                File.Copy(path, path + ".previous", true);
            }
            SafeFiles.AtomicWrite(path, json);
        }
    }
    public ConnectionProfile Active => State.Profiles.FirstOrDefault(p => p.Id == State.ActiveProfileId)
        ?? throw new UserException("请先添加并选择一个 API 连接。");
    public string? Key(ConnectionProfile profile) => Vault.Get(profile.SecretRef.Length > 0 ? profile.SecretRef : profile.Id);
    public void SaveProfile(ConnectionProfile profile, string? key)
    {
        UrlPolicy.Base(profile.BaseUrl);
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new UserException("请输入连接名称。");
        string oldPrevious = "";
        if (key is not null)
        {
            if (key.Length < 5 || key.Length > 8192 || key.Any(char.IsControl)) throw new UserException("API Key 格式无效。");
            oldPrevious = profile.PreviousSecretRef;
            var reference = profile.Id + "-" + Guid.NewGuid().ToString("N")[..12];
            Vault.Put(reference, key.Trim());
            profile.PreviousSecretRef = profile.SecretRef.Length > 0 ? profile.SecretRef : Vault.Contains(profile.Id) ? profile.Id : "";
            profile.SecretRef = reference;
        }
        if (Key(profile) is null) throw new UserException("请输入 API Key。");
        var index = State.Profiles.FindIndex(x => x.Id == profile.Id);
        if (index >= 0) State.Profiles[index] = profile; else State.Profiles.Add(profile);
        State.ActiveProfileId = profile.Id;
        Save();
        if (oldPrevious.Length > 0 && oldPrevious != profile.SecretRef && oldPrevious != profile.PreviousSecretRef)
            Vault.Remove(oldPrevious);
    }
    public void RemoveProfile(ConnectionProfile profile)
    {
        if (State.Clients.Any(c => c.ProfileId == profile.Id))
            throw new UserException("此连接仍被客户端使用。请先为该客户端切换连接，再删除。");
        Vault.Remove(profile.Id);
        if (profile.SecretRef.Length > 0) Vault.Remove(profile.SecretRef);
        if (profile.PreviousSecretRef.Length > 0) Vault.Remove(profile.PreviousSecretRef);
        State.Profiles.RemoveAll(p => p.Id == profile.Id);
        if (State.ActiveProfileId == profile.Id) State.ActiveProfileId = State.Profiles.FirstOrDefault()?.Id ?? "";
        Save();
    }
}

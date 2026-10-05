using LiteDB;
using UDRoute.Maui.Models;

namespace UDRoute.Maui.Data;

public class LiteDbContext
{
    private readonly string _dbPath;

    public LiteDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public ILiteCollection<Scene> Scenes
    {
        get
        {
            var db = new LiteDatabase(_dbPath);
            return db.GetCollection<Scene>("scenes");
        }
    }
}

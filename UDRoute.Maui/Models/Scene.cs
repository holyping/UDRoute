using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;
using UDRoute;

namespace UDRoute.Maui.Models;

public partial class Scene : ObservableObject
{
    [BsonId]
    public int Id { get; set; }

    [ObservableProperty]
    private string _name = "新建场境";

    [ObservableProperty]
    private bool _isSelected;

    public AppConfig Config { get; set; } = new AppConfig();
}

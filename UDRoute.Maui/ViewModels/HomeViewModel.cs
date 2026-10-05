using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using UDRoute.Maui.Data;
using UDRoute.Maui.Models;
using UDRoute.Maui.Services;
using UDRoute.Maui.Views;

namespace UDRoute.Maui.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly LiteDbContext _db;
    public Services.EngineService Engine { get; }

    private List<Scene> _allScenes = new();

    [ObservableProperty]
    private ObservableCollection<Scene> _scenes = new();

    [ObservableProperty]
    private Scene? _selectedScene;

    [ObservableProperty]
    private bool _isAddMenuVisible = false;

    [ObservableProperty]
    private string _searchText = "";

    public HomeViewModel(LiteDbContext db, Services.EngineService engine)
    {
        _db = db;
        Engine = engine;
        LoadScenes();
    }

    public void LoadScenes()
    {
        _allScenes = _db.Scenes.FindAll().ToList();
        if (_allScenes.Count == 0)
        {
            var defaultScene1 = new Scene { Name = "家庭网络", IsSelected = true };
            var defaultScene2 = new Scene { Name = "公司内网", IsSelected = false };
            _db.Scenes.Insert(defaultScene1);
            _db.Scenes.Insert(defaultScene2);
            _allScenes = new List<Scene> { defaultScene1, defaultScene2 };
        }

        FilterScenes();

        SelectedScene = Scenes.FirstOrDefault(s => s.IsSelected) ?? Scenes.FirstOrDefault();
        if (SelectedScene != null)
        {
            SelectedScene.IsSelected = true;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterScenes();
    }

    private void FilterScenes()
    {
        Scenes.Clear();
        var filtered = string.IsNullOrWhiteSpace(SearchText) 
            ? _allScenes 
            : _allScenes.Where(s => s.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var s in filtered)
        {
            Scenes.Add(s);
        }
    }

    [RelayCommand]
    private void ToggleAddMenu()
    {
        IsAddMenuVisible = !IsAddMenuVisible;
    }

    [RelayCommand]
    private void CloseAddMenu()
    {
        IsAddMenuVisible = false;
    }

    [RelayCommand]
    public void SelectScene(Scene scene)
    {
        if (scene == null) return;

        foreach (var s in _allScenes)
        {
            s.IsSelected = (s.Id == scene.Id);
            _db.Scenes.Update(s);
        }

        SelectedScene = scene;
        if (!Engine.IsRunning)
        {
            Engine.CurrentStatusText = $"已就绪: {scene.Name}";
        }
    }

    [RelayCommand]
    private async Task ShareSceneAsync(Scene scene)
    {
        if (scene == null) return;
        var shareVm = new ShareViewModel(scene);
        var sharePage = new SharePage(shareVm);

        if (Shell.Current != null)
        {
            await Shell.Current.Navigation.PushAsync(sharePage);
        }
        else if (Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation != null)
        {
            await Application.Current.Windows[0].Page!.Navigation.PushAsync(sharePage);
        }
    }

    [RelayCommand]
    private async Task EditSceneAsync(Scene scene)
    {
        if (scene == null) return;
        var editVm = new SceneEditViewModel(_db, scene.Id);
        var editPage = new SceneEditPage(editVm);

        if (Shell.Current != null)
        {
            await Shell.Current.Navigation.PushAsync(editPage);
        }
        else if (Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation != null)
        {
            await Application.Current.Windows[0].Page!.Navigation.PushAsync(editPage);
        }
    }

    [RelayCommand]
    private async Task AddManualAsync()
    {
        IsAddMenuVisible = false;

        var editVm = new SceneEditViewModel(_db, 0);
        var editPage = new SceneEditPage(editVm);

        if (Shell.Current != null)
        {
            await Shell.Current.Navigation.PushAsync(editPage);
        }
        else if (Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation != null)
        {
            await Application.Current.Windows[0].Page!.Navigation.PushAsync(editPage);
        }
    }

    [RelayCommand]
    private async Task AddFromQrScanAsync()
    {
        IsAddMenuVisible = false;

        var scanPage = new QrScanPage(_db);

        if (Shell.Current != null)
        {
            await Shell.Current.Navigation.PushAsync(scanPage);
        }
        else if (Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation != null)
        {
            await Application.Current.Windows[0].Page!.Navigation.PushAsync(scanPage);
        }
    }

    [RelayCommand]
    private async Task AddFromClipboardAsync()
    {
        IsAddMenuVisible = false;

        try
        {
            string clipText = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrWhiteSpace(clipText))
            {
                if (Application.Current?.Windows.FirstOrDefault()?.Page != null)
                {
                    await Application.Current.Windows[0].Page!.DisplayAlert("提示", "系统剪贴板为空，无法读取场境配置", "确定");
                }
                return;
            }

            var importedScene = SceneTransferService.Import(clipText);
            if (importedScene == null)
            {
                if (Application.Current?.Windows.FirstOrDefault()?.Page != null)
                {
                    await Application.Current.Windows[0].Page!.DisplayAlert("解析失败", "剪贴板中的内容不符合 UDRoute 场境配置格式", "确定");
                }
                return;
            }

            importedScene.Name += " (剪贴板导入)";
            _db.Scenes.Insert(importedScene);
            LoadScenes();
            SelectScene(importedScene);

            if (Application.Current?.Windows.FirstOrDefault()?.Page != null)
            {
                await Application.Current.Windows[0].Page!.DisplayAlert("导入成功", $"已成功从剪贴板导入并新增场境：{importedScene.Name}", "确定");
            }
        }
        catch (Exception ex)
        {
            if (Application.Current?.Windows.FirstOrDefault()?.Page != null)
            {
                await Application.Current.Windows[0].Page!.DisplayAlert("错误", $"读取剪贴板异常: {ex.Message}", "确定");
            }
        }
    }

    [RelayCommand]
    private void DeleteScene(Scene scene)
    {
        if (scene == null) return;
        _db.Scenes.Delete(scene.Id);
        _allScenes.RemoveAll(s => s.Id == scene.Id);
        Scenes.Remove(scene);

        if (SelectedScene?.Id == scene.Id)
        {
            SelectedScene = Scenes.FirstOrDefault();
            if (SelectedScene != null)
            {
                SelectScene(SelectedScene);
            }
        }
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (!Engine.IsRunning)
        {
            if (SelectedScene == null)
            {
                Engine.CurrentStatusText = "请先选择一个场境";
                return;
            }

            await Engine.StartAsync(SelectedScene);
        }
        else
        {
            Engine.Stop();
        }
    }
}

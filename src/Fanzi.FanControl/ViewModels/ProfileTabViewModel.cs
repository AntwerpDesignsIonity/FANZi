using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fanzi.FanControl.Models;
using System;

namespace Fanzi.FanControl.ViewModels;

public partial class ProfileTabViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isRenaming;

    public FanProfile Profile { get; }

    public IRelayCommand SelectCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand BeginRenameCommand { get; }
    public IRelayCommand CommitRenameCommand { get; }

    public ProfileTabViewModel(
        FanProfile profile,
        Action<ProfileTabViewModel> onSelect,
        Action<ProfileTabViewModel> onDelete,
        Action<ProfileTabViewModel, string> onRename)
    {
        Profile = profile;
        _name = profile.Name;

        SelectCommand = new RelayCommand(() => onSelect(this));
        DeleteCommand = new RelayCommand(() => onDelete(this));
        BeginRenameCommand = new RelayCommand(() => IsRenaming = true);
        CommitRenameCommand = new RelayCommand(() =>
        {
            string trimmed = Name.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                Name = Profile.Name;
            }
            else
            {
                onRename(this, trimmed);
            }
            IsRenaming = false;
        });
    }
}

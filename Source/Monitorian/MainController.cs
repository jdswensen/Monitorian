﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using Monitorian.Models;
using Monitorian.Models.Monitor;
using Monitorian.Models.Watcher;
using Monitorian.ViewModels;
using Monitorian.Views;
using ScreenFrame;

namespace Monitorian
{
	public class MainController
	{
		private readonly Application _current = Application.Current;

		public Settings Settings { get; }

		public ObservableCollection<MonitorViewModel> Monitors { get; }
		private readonly object _monitorsLock = new object();

		public NotifyIconComponent NotifyIconComponent { get; }

		private readonly SettingsWatcher _settingsWatcher;
		private readonly PowerWatcher _powerWatcher;
		private readonly BrightnessWatcher _brightnessWatcher;

		public MainController()
		{
			Settings = new Settings();

			Monitors = new ObservableCollection<MonitorViewModel>();
			BindingOperations.EnableCollectionSynchronization(Monitors, _monitorsLock);

			NotifyIconComponent = new NotifyIconComponent();
			NotifyIconComponent.MouseLeftButtonClick += OnMainWindowShowRequested;
			NotifyIconComponent.MouseRightButtonClick += OnMenuWindowShowRequested;

			_settingsWatcher = new SettingsWatcher();
			_powerWatcher = new PowerWatcher();
			_brightnessWatcher = new BrightnessWatcher();
		}

		internal async Task InitiateAsync(RemotingAgent agent)
		{
			if (agent == null)
				throw new ArgumentNullException(nameof(agent));

			Settings.Load();
			
			NotifyIconComponent.ShowIcon("pack://application:,,,/Resources/Icons/TrayIcon.ico", ProductInfo.Title);

			_current.MainWindow = new MainWindow(this);
			_current.MainWindow.DpiChanged += OnDpiChanged;

			if (!StartupService.IsStartedOnSignIn(Settings.LastCloseTime))
				_current.MainWindow.Show();

			agent.ShowRequested += OnMainWindowShowRequested;

			await ScanAsync();

			_settingsWatcher.Subscribe(() => ScanAsync());
			_powerWatcher.Subscribe(() => ScanAsync());
			_brightnessWatcher.Subscribe((instanceName, brightness) => Update(instanceName, brightness));
		}

		internal void End()
		{
			MonitorsDispose();
			NotifyIconComponent.Dispose();

			Settings.SaveOnClose();

			_settingsWatcher.Dispose();
			_powerWatcher.Dispose();
			_brightnessWatcher.Dispose();
		}

		private async void OnMainWindowShowRequested(object sender, EventArgs e)
		{
			ShowMainWindow();
			await UpdateAsync();
		}

		private void OnMenuWindowShowRequested(object sender, Point e)
		{
			ShowMenuWindow(e);
		}

		private void OnDpiChanged(object sender, DpiChangedEventArgs e)
		{
			NotifyIconComponent.AdjustIcon(e.NewDpi);
		}

		private async void ShowMainWindow()
		{
			var window = (MainWindow)_current.MainWindow;
			if (!window.CanBeShown)
				return;

			if (window.Visibility != Visibility.Visible)
			{
				window.Show();
				window.Activate();
			}
			await UpdateAsync();
		}

		private void ShowMenuWindow(Point pivot)
		{
			var window = new MenuWindow(this, pivot);
			window.ViewModel.CloseAppRequested += (sender, e) => _current.Shutdown();
			window.Show();
		}

		#region Monitors

		internal event EventHandler<bool> ScanningChanged;

		private static readonly Lazy<int> _maxMonitorCount = new Lazy<int>(() =>
		{
			int count = 4;
			SetCount(ref count);
			return count;
		});

		private static readonly Lazy<int> _maxNameCount = new Lazy<int>(() => _maxMonitorCount.Value * 4);

		private int _scanCount = 0;
		private int _updateCount = 0;

		private async Task ScanAsync()
		{
			var isEntered = false;
			try
			{
				isEntered = (Interlocked.Increment(ref _scanCount) == 1);
				if (isEntered)
				{
					ScanningChanged?.Invoke(this, true);

					var scanTime = DateTimeOffset.Now;

					await Task.Run(() =>
					{
						var oldMonitors = Monitors.ToList();

						foreach (var item in MonitorManager.EnumerateMonitors())
						{
							var oldMonitor = oldMonitors.FirstOrDefault(x =>
								string.Equals(x.DeviceInstanceId, item.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
							if (oldMonitor != null)
							{
								oldMonitors.Remove(oldMonitor);
								item.Dispose();
								continue;
							}

							var newMonitor = new MonitorViewModel(item);
							FindName(newMonitor);
							if (Monitors.Count < _maxMonitorCount.Value)
							{
								newMonitor.UpdateBrightness();
								newMonitor.IsTarget = true;
							}
							lock (_monitorsLock)
							{
								Monitors.Add(newMonitor);
							}
						}

						foreach (var oldMonitor in oldMonitors)
						{
							oldMonitor.Dispose();
							lock (_monitorsLock)
							{
								Monitors.Remove(oldMonitor);
							}
						}
					});

					await Task.WhenAll(Monitors
						.Take(_maxMonitorCount.Value)
						.Where(x => x.UpdateTime < scanTime)
						.Select(x => Task.Run(() =>
						{
							x.UpdateBrightness();
							x.IsTarget = true;
						})));
				}
			}
			finally
			{
				if (isEntered)
				{
					ScanningChanged?.Invoke(this, false);

					Interlocked.Exchange(ref _scanCount, 0);
				}
			}
		}

		private async Task UpdateAsync()
		{
			if (_scanCount > 0)
				return;

			var isEntered = false;
			try
			{
				isEntered = (Interlocked.Increment(ref _updateCount) == 1);
				if (isEntered)
				{
					await Task.WhenAll(Monitors
						.Where(x => x.IsTarget)
						.Select(x => Task.Run(() => x.UpdateBrightness())));
				}
			}
			finally
			{
				if (isEntered)
				{
					Interlocked.Exchange(ref _updateCount, 0);
				}
			}
		}

		private void Update(string instanceName, int brightness)
		{
			var monitor = Monitors.FirstOrDefault(x => instanceName.StartsWith(x.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
			if (monitor != null)
			{
				monitor.UpdateBrightness(brightness);
			}
		}

		private void MonitorsDispose()
		{
			foreach (var monitor in Monitors)
			{
				StoreName(monitor);
				monitor.Dispose();
			}
			TruncateNames();
		}

		private void FindName(MonitorViewModel monitor)
		{
			if (Settings.KnownMonitors.TryGetValue(monitor.DeviceInstanceId, out NamePack knownMonitor))
			{
				monitor.Name = knownMonitor.Name;
			}
		}

		private void StoreName(MonitorViewModel monitor)
		{
			if (!Settings.KnownMonitors.ContainsKey(monitor.DeviceInstanceId))
			{
				if (monitor.HasName)
				{
					// Add
					Settings.KnownMonitors.Add(monitor.DeviceInstanceId, new NamePack(monitor.Name));
				}
			}
			else
			{
				if (monitor.HasName)
				{
					// Modify
					Settings.KnownMonitors[monitor.DeviceInstanceId] = new NamePack(monitor.Name);
				}
				else
				{
					// Remove
					Settings.KnownMonitors.Remove(monitor.DeviceInstanceId);
				}
			}
		}

		private void TruncateNames()
		{
			if (Settings.KnownMonitors.Count <= _maxNameCount.Value)
				return;

			foreach (var key in Settings.KnownMonitors
				.OrderByDescending(x => x.Value.Time)
				.Skip(_maxNameCount.Value)
				.Select(x => x.Key)
				.ToArray())
			{
				Settings.KnownMonitors.Remove(key);
			}
		}

		#endregion

		#region Configuration

		[Conditional("UNLIMITED")]
		private static void SetCount(ref int count)
		{
			count *= 8;
		}

		#endregion
	}
}
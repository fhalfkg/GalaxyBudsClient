﻿using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GalaxyBudsClient.Decoder;
using GalaxyBudsClient.Interface.Items;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Model.Attributes;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Platform;
using GalaxyBudsClient.Utils;
using GalaxyBudsClient.Utils.DynamicLocalization;
using Serilog;

namespace GalaxyBudsClient.Interface.Pages
{
 	public class TouchpadPage : AbstractPage
	{
		public override Pages PageType => Pages.Touch;
		
		private readonly SwitchListItem _lock;
		private readonly SwitchDetailListItem _edgeTouch;
		private readonly MenuListItem _leftOption;
		private readonly MenuListItem _rightOption;
		
		private TouchOptions _lastLeftOption;
		private TouchOptions _lastRightOption;
		
		public TouchpadPage()
		{   
			AvaloniaXamlLoader.Load(this);
			_lock = this.FindControl<SwitchListItem>("LockToggle");
			_edgeTouch = this.FindControl<SwitchDetailListItem>("DoubleTapVolume");
			_leftOption = this.FindControl<MenuListItem>("LeftOption");
			_rightOption = this.FindControl<MenuListItem>("RightOption");

		
			SPPMessageHandler.Instance.ExtendedStatusUpdate += InstanceOnExtendedStatusUpdate;
			
			Loc.LanguageUpdated += UpdateTouchActionMenus;
			Loc.LanguageUpdated += UpdateMenuDescriptions;
			UpdateTouchActionMenus();
		}
		
		private void InstanceOnExtendedStatusUpdate(object? sender, ExtendedStatusUpdateParser e)
		{
			_lock.IsChecked = e.TouchpadLock;
			_edgeTouch.IsChecked = e.OutsideDoubleTap;

			_lastLeftOption = e.TouchpadOptionL;
			_lastRightOption = e.TouchpadOptionR;

			_leftOption.Description = e.TouchpadOptionL.GetDescription();
			_rightOption.Description = e.TouchpadOptionR.GetDescription();
			
			UpdateMenuDescriptions();
		}

		private void UpdateMenuDescriptions()
		{
			if (_lastLeftOption == TouchOptions.OtherL)
			{
				_leftOption.Description =
					$"{Loc.Resolve("touchoption_custom_prefix")} {new CustomAction(SettingsProvider.Instance.CustomActionLeft.Action, SettingsProvider.Instance.CustomActionLeft.Parameter)}";
			}
			else
			{
				_leftOption.Description = _lastLeftOption.GetDescription();
			}

			if (_lastRightOption == TouchOptions.OtherR)
			{
				_rightOption.Description =
					$"{Loc.Resolve("touchoption_custom_prefix")} {new CustomAction(SettingsProvider.Instance.CustomActionRight.Action, SettingsProvider.Instance.CustomActionRight.Parameter)}";
			}
			else
			{
				_rightOption.Description = _lastRightOption.GetDescription();
			}
		}
		
		private void UpdateTouchActionMenus()
		{
			foreach (var obj in Enum.GetValues(typeof(Devices)))
			{
				if (obj is Devices device)
				{
					var menuActions = new Dictionary<string,EventHandler<RoutedEventArgs>?>();
                    foreach (var value in BluetoothImpl.Instance.DeviceSpec.TouchMap.LookupTable)
                    {
                        if (value.Key.IsHidden())
                            continue;
                        
                        menuActions.Add(value.Key.GetDescription(), (sender, args) => ItemClicked(device, value.Key));
                    }
                    
                    /* Inject custom actions if appropriate */
                    if (BluetoothImpl.Instance.DeviceSpec.TouchMap.LookupTable.ContainsKey(TouchOptions.OtherL) || 
                        BluetoothImpl.Instance.DeviceSpec.TouchMap.LookupTable.ContainsKey(TouchOptions.OtherR))
                    {
	                    menuActions.Add(Loc.Resolve("touchoption_custom"), (sender, args) =>
		                    ItemClicked(device, device == Devices.L ? TouchOptions.OtherL : TouchOptions.OtherR));
                    }

                    switch (device)
                    {
	                    case Devices.L:
		                    _leftOption.Items = menuActions;
		                    break;
	                    case Devices.R:
		                    _rightOption.Items = menuActions;
		                    break;
                    }
				}
			}
		}

		private async void ItemClicked(Devices device, TouchOptions option)
		{
			if (option == TouchOptions.OtherL || option == TouchOptions.OtherR)
			{
				/* Open custom action selector first and await user input,
				 do not send the updated options ids just yet  */
                MainWindow.Instance.ShowCustomActionSelection(device);
			}
			else
			{
				if (device == Devices.L)
				{
					_lastLeftOption = option;
				}
				else
				{
					_lastRightOption = option;
				}
				
				UpdateMenuDescriptions();
				await MessageComposer.Touch.SetOptions(_lastLeftOption, _lastRightOption);
            }
			
		}
		
		private async void CustomTouchActionPageOnAccepted(object? sender, CustomAction e)
		{
			if (MainWindow.Instance.CustomTouchActionPage.CurrentSide == Devices.L)
			{
				_lastLeftOption = TouchOptions.OtherL;
				_leftOption.Description = $"{Loc.Resolve("touchoption_custom_prefix")} {e}";
			}
			else
			{
				_lastRightOption = TouchOptions.OtherR;
				_rightOption.Description = $"{Loc.Resolve("touchoption_custom_prefix")} {e}"; 
			}

			await MessageComposer.Touch.SetOptions(_lastLeftOption, _lastRightOption);
			
			// TODO: Force enable MinimizeTray and AutoStart here!
		}

		
		public override void OnPageShown()
		{
			_edgeTouch.Parent.IsVisible =
				BluetoothImpl.Instance.DeviceSpec.Supports(IDeviceSpec.Feature.DoubleTapVolume);
			
			MainWindow.Instance.CustomTouchActionPage.Accepted += CustomTouchActionPageOnAccepted;

			UpdateMenuDescriptions();
		}
		
		private void BackButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
		{
			MainWindow.Instance.Pager.SwitchPage(Pages.Home);
		}

		private async void LockToggle_OnToggled(object? sender, bool e)
		{
			await BluetoothImpl.Instance.SendRequestAsync(SPPMessage.MessageIds.MSG_ID_LOCK_TOUCHPAD, e);
		}

		private async void DoubleTapVolume_OnToggled(object? sender, bool e)
		{
			await BluetoothImpl.Instance.SendRequestAsync(SPPMessage.MessageIds.MSG_ID_OUTSIDE_DOUBLE_TAP, e);
		}
	}
}
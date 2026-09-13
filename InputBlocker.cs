using System;
using System.Reflection;
using Candide.Input;
using Candide.Input.InputProviders.Native;
using Microsoft.Xna.Framework.Input;

namespace RomesteadCheatMenu;

// Ctrl+0 toggles the menu. While it is open (and until every key/button is released
// after closing) the game sees no keyboard, mouse or gamepad input.
// While the terraforming brush is active the player can still walk, but every game action
// and the mouse buttons belong to the brush.
// Dear ImGui reads Keyboard/Mouse state directly, so the menu itself keeps working.
internal static class InputBlocker
{
    // The game's own windows (chat, favours, trading post) stop the player controller the same way.
    private const string GameBlockerId = "RomesteadCheatMenu";

    private static readonly FieldInfo MouseButtonsField = typeof(MouseDataState).GetField("_buttonStates", BindingFlags.Instance | BindingFlags.NonPublic);

    private static bool _hotkeyWasDown;
    private static bool _suppressUntilRelease;
    private static bool _gameBlockerAdded;

    public static bool Blocking => CheatMenu.IsOpen || _suppressUntilRelease;

    // Chat and the dev terminal read the keyboard on their own.
    public static bool BlocksTextInput => Blocking || Terraform.BrushActive;

    public static void BeforeInputUpdate()
    {
        KeyboardState keyboard = Keyboard.GetState();
        bool ctrl = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        bool hotkey = ctrl && (keyboard.IsKeyDown(Keys.D0) || keyboard.IsKeyDown(Keys.NumPad0));
        if (hotkey && !_hotkeyWasDown)
        {
            if (Terraform.BrushActive)
            {
                Terraform.StopBrush(reopenMenu: true);
            }
            else if (CheatMenu.IsOpen)
            {
                CheatMenu.Close();
            }
            else if (GameAccess.InWorld)
            {
                CheatMenu.Open();
            }
        }
        _hotkeyWasDown = hotkey;

        // Never leave the game without input if the menu cannot be shown.
        if (CheatMenu.IsOpen && (!GameAccess.InWorld || CheatMenu.DrawIsStale))
        {
            CheatMenu.Close();
        }

        Terraform.BeforeInput(keyboard);

        if (!CheatMenu.IsOpen && _suppressUntilRelease && keyboard.GetPressedKeyCount() == 0 && !AnyMouseButtonDown())
        {
            _suppressUntilRelease = false;
        }
    }

    public static void AfterInputUpdate()
    {
        bool blocking = Blocking;
        SetGameBlocker(blocking);
        if (blocking)
        {
            // Walking (WASD and sticks) is analog: the InputManager.GetStickPadAxis patch answers zero.
            InputManager.ConsumeAll();
            InputManager.StoreStates();
            KeepScrollWheel();
            return;
        }
        if (Terraform.BrushActive)
        {
            // Digital actions (attack, interact, hotbar, menus) are consumed; walking keeps working.
            InputManager.ConsumeAll();
            ReleaseMouseButtons();
            KeepScrollWheel();
        }
    }

    public static void OnMenuClosed()
    {
        _suppressUntilRelease = true;
    }

    private static void SetGameBlocker(bool block)
    {
        if (block)
        {
            PlayerControllerKeyBinds.InputBlockers.Add(GameBlockerId);
            _gameBlockerAdded = true;
        }
        else if (_gameBlockerAdded)
        {
            PlayerControllerKeyBinds.InputBlockers.Remove(GameBlockerId);
            _gameBlockerAdded = false;
        }
    }

    // StoreStates pins the wheel to its old value; keep it current so the wheel
    // movement made inside the menu or the brush is not replayed into the game afterwards.
    private static void KeepScrollWheel()
    {
        MouseState mouse = Mouse.GetState();
        NativeInput.OldMouseState.ScrollWheelValue = mouse.ScrollWheelValue;
        NativeInput.NewMouseState.ScrollWheelValue = mouse.ScrollWheelValue;
        NativeInput.OldMouseState.HorizontalScrollWheelValue = mouse.HorizontalScrollWheelValue;
        NativeInput.NewMouseState.HorizontalScrollWheelValue = mouse.HorizontalScrollWheelValue;
        NativeInput.MouseScrollVerticalDelta = 0f;
        NativeInput.MouseScrollHorizontalDelta = 0f;
    }

    // The HUD and build tools read mouse buttons straight from NativeInput.
    private static void ReleaseMouseButtons()
    {
        ClearButtons(NativeInput.OldMouseState);
        ClearButtons(NativeInput.NewMouseState);
        NativeInput.OldMouseState.State = WithoutButtons(NativeInput.OldMouseState.State);
        NativeInput.NewMouseState.State = WithoutButtons(NativeInput.NewMouseState.State);
    }

    private static void ClearButtons(MouseDataState state)
    {
        // MouseDataState is a struct, but the button array inside it is shared.
        if (MouseButtonsField?.GetValue(state) is bool[] buttons)
        {
            Array.Clear(buttons);
        }
    }

    private static MouseState WithoutButtons(MouseState state)
    {
        return new MouseState(state.X, state.Y, state.ScrollWheelValue, ButtonState.Released, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, state.HorizontalScrollWheelValue);
    }

    private static bool AnyMouseButtonDown()
    {
        MouseState mouse = Mouse.GetState();
        return mouse.LeftButton == ButtonState.Pressed || mouse.RightButton == ButtonState.Pressed || mouse.MiddleButton == ButtonState.Pressed
            || mouse.XButton1 == ButtonState.Pressed || mouse.XButton2 == ButtonState.Pressed;
    }
}

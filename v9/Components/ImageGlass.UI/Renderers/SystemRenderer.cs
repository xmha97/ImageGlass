﻿/*
ImageGlass Project - Image viewer for Windows
Copyright (C) 2010 - 2022 DUONG DIEU PHAP
Project homepage: https://imageglass.org

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System.Runtime.InteropServices;

namespace ImageGlass.UI;

public static class SystemRenderer
{
    [DllImport("uxtheme.dll")]
    private static extern int SetWindowTheme(
        [In] IntPtr hwnd,
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszSubAppName,
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszSubIdList
    );

    /// <summary>
    /// Apply system theme to control
    /// </summary>
    /// <param name="control"></param>
    /// <returns></returns>
    public static int ApplyTheme(Control? control, bool darkMode = true)
    {
        return ApplyTheme(control, darkMode ? "DarkMode_Explorer" : "Explorer");
    }

    /// <summary>
    /// Apply system theme to control (customize)
    /// </summary>
    public static int ApplyTheme(Control? control, string theme)
    {
        try
        {
            if (control != null)
            {
                if (control.IsHandleCreated)
                {
                    return SetWindowTheme(control.Handle, theme, string.Empty);
                }
            }
        }
        catch
        {
            return 0;
        }
        return 1;
    }

    /// <summary>
    /// Remove system theme
    /// </summary>
    /// <param name="control"></param>
    /// <returns></returns>
    public static int ClearTheme(Control control)
    {
        return ApplyTheme(control, string.Empty);
    }
}

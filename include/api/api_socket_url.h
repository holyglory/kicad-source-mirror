/*
 * This file is part of KiCad, licensed under the GNU GPL version 3 or later.
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 */

#pragma once

#include <algorithm>
#include <string>

// NNG treats everything after ipc:// as a literal local name. Windows named
// pipes cannot contain backslashes; use the same forward-slash spelling as the
// managed client. This is not a file URI and must not be URL-escaped.
inline std::string KiApiSocketUrl( std::string aAbsolutePath )
{
#ifdef _WIN32
    std::replace( aAbsolutePath.begin(), aAbsolutePath.end(), '\\', '/' );
#endif
    return "ipc://" + aAbsolutePath;
}

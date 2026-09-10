/*
 * This file is part of KiCad, licensed under the GNU GPL version 3 or later.
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 */

#pragma once

#include <string>

// NNG treats everything after ipc:// as a literal local name. Preserve the
// native path spelling on every platform. This is not a file URI and must not
// be URL-escaped or have its separators rewritten by only one participant.
inline std::string KiApiSocketUrl( std::string aAbsolutePath )
{
    return "ipc://" + aAbsolutePath;
}

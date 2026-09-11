/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once

#include <json_common.h>
#include <string>

namespace AUTOMATION_WINDOWS_UPDATE
{
/** Snapshot this process and the current selection for an explicit Update click.
 * Does not activate a version, write a journal, or close an editor. The managed
 * handoff verifies the signed payload and these exact identities before ack. */
nlohmann::json RestartRequest( const std::string& aInstallationRoot,
        const std::string& aProjectPath, const std::string& aManifestSha256,
        const std::string& aOperationId, const std::string& aInstanceId,
        bool aSoftwareRendering );
}

#ifdef USE_KICAD_JSON_IMPORTS
#include <json_common.h>
#else
#include <nlohmann/json.hpp>
#include <kicommon.h>
#endif

extern "C" KICOMMON_API int fixture_touch();
int imported_json_size();

int main()
{
    nlohmann::json value = { { "schemaVersion", 1 }, { "status", "available" } };
    nlohmann::json copy = value;
    value = copy;
    return value.size() == 2 && fixture_touch() == 2 && imported_json_size() == 2 ? 0 : 1;
}

#include <windows.h>
#include <fstream>
#include <string>

static std::string savedPath;
static bool saved = false;
static bool blank = false;

static void Paint( HWND window, HDC dc )
{
    RECT client; GetClientRect( window, &client );
    FillRect( dc, &client, reinterpret_cast<HBRUSH>( COLOR_WINDOW + 1 ) );
    if( blank ) return;
    RECT block = { client.right / 4, client.bottom / 3, client.right / 2, client.bottom * 2 / 3 };
    HBRUSH brush = CreateSolidBrush( saved ? RGB( 35, 140, 75 ) : RGB( 40, 90, 190 ) );
    FillRect( dc, &block, brush ); DeleteObject( brush );
    const char* text = saved ? "Saved through native keyboard input" : "Unsaved native UI fixture";
    SetBkMode( dc, TRANSPARENT );
    TextOutA( dc, client.right / 4, client.bottom / 4, text, lstrlenA( text ) );
}

static LRESULT CALLBACK WindowProc( HWND window, UINT message, WPARAM wParam, LPARAM lParam )
{
    if( message == WM_KEYDOWN && wParam == 'S' && ( GetKeyState( VK_CONTROL ) & 0x8000 ) )
    {
        std::ofstream result( savedPath );
        result << "saved by native Ctrl+S";
        result.close();
        saved = true;
        SetWindowTextA( window, "KiCad native UI fixture - Saved" );
        InvalidateRect( window, nullptr, TRUE );
        return 0;
    }
    if( message == WM_KEYDOWN && wParam == 'B' && ( GetKeyState( VK_CONTROL ) & 0x8000 ) )
    {
        blank = !blank;
        SetWindowTextA( window, blank ? "KiCad native UI fixture - Blank" : "KiCad native UI fixture - Saved" );
        InvalidateRect( window, nullptr, TRUE );
        return 0;
    }
    if( message == WM_PRINTCLIENT ) { Paint( window, reinterpret_cast<HDC>( wParam ) ); return 0; }
    if( message == WM_PAINT )
    {
        PAINTSTRUCT paint;
        HDC dc = BeginPaint( window, &paint );
        Paint( window, dc );
        EndPaint( window, &paint );
        return 0;
    }
    if( message == WM_DESTROY ) { PostQuitMessage( 0 ); return 0; }
    return DefWindowProcA( window, message, wParam, lParam );
}

int main( int argc, char** argv )
{
    if( argc != 2 ) return 1;
    savedPath = argv[1];
    HINSTANCE instance = GetModuleHandleA( nullptr );
    WNDCLASSA cls = {};
    cls.lpfnWndProc = WindowProc; cls.hInstance = instance; cls.lpszClassName = "KiCadNativeUiFixture";
    cls.hCursor = LoadCursor( nullptr, IDC_ARROW );
    if( !RegisterClassA( &cls ) ) return 2;
    HWND window = CreateWindowA( cls.lpszClassName, "KiCad native UI fixture", WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, 760, 500, nullptr, nullptr, instance, nullptr );
    if( !window ) return 3;
    ShowWindow( window, SW_SHOW ); UpdateWindow( window );
    MSG message;
    while( GetMessage( &message, nullptr, 0, 0 ) > 0 )
    { TranslateMessage( &message ); DispatchMessage( &message ); }
    return saved ? 0 : 4;
}

/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <kiplatform/ui.h>
#include <wx/app.h>
#include <wx/frame.h>
#include <wx/evtloop.h>
#include <wx/init.h>
#include <wx/timer.h>
#include <wx/uiaction.h>
#include <gtk/gtk.h>
#include <boost/test/unit_test.hpp>

namespace
{
void waitFor( const std::function<bool()>& condition )
{
    wxEventLoop loop;
    wxEventLoopActivator active( &loop );
    wxEvtHandler events;
    wxTimer timer( &events );
    wxStopWatch time;
    bool expired = false;
    events.Bind( wxEVT_TIMER, [&]( wxTimerEvent& )
    {
        expired = time.Time() >= 5000;
        if( condition() || expired ) loop.Exit();
    } );
    timer.Start( 10 );
    loop.Run();
    timer.Stop();
    BOOST_REQUIRE( !expired );
}

void click( GtkWidget* button, wxFrame* frame )
{
    GtkWidget* window = GTK_WIDGET( frame->GetHandle() );
    int x = 0, y = 0, originX = 0, originY = 0;
    BOOST_REQUIRE( gtk_widget_translate_coordinates( button, window, 0, 0, &x, &y ) );
    gdk_window_get_origin( gtk_widget_get_window( window ), &originX, &originY );
    wxUIActionSimulator input;
    BOOST_REQUIRE( input.MouseMove( originX + x + gtk_widget_get_allocated_width( button ) / 2,
                                   originY + y + gtk_widget_get_allocated_height( button ) / 2 ) );
    BOOST_REQUIRE( input.MouseClick() );
    gdk_display_sync( gtk_widget_get_display( window ) );
    wxTheApp->Yield( true );
}
}

BOOST_AUTO_TEST_CASE( CaptionActionStartupTitleMouseAndVisibility )
{
    BOOST_TEST_MESSAGE( "Creating native frame" );
    wxFrame* frame = new wxFrame( nullptr, wxID_ANY, "Caption fixture", wxDefaultPosition, wxSize( 500, 300 ) );
    BOOST_TEST_MESSAGE( "Adding native caption action" );
    int clicks = 0;
    auto action = KIPLATFORM::UI::AddCaptionAction( frame, "Update", [&] { ++clicks; } );
    BOOST_TEST_MESSAGE( "Showing native frame" );
    action( true, true );
    frame->Show();
    frame->SetTitle( "Updated caption fixture" );
    GtkWidget* header = gtk_window_get_titlebar( GTK_WINDOW( frame->GetHandle() ) );
    waitFor( [&] { return gtk_widget_get_mapped( header )
        && std::string( gtk_header_bar_get_title( GTK_HEADER_BAR( header ) ) ) == "Updated caption fixture"; } );
    BOOST_CHECK( frame->IsShown() );
    GtkWidget* button = nullptr;
    GList* children = gtk_container_get_children( GTK_CONTAINER( header ) );
    for( GList* child = children; child; child = child->next )
    {
        GtkWidget* widget = GTK_WIDGET( child->data );
        if( GTK_IS_BUTTON( widget ) && g_strcmp0( gtk_button_get_label( GTK_BUTTON( widget ) ), "Update" ) == 0 ) button = widget;
    }
    g_list_free( children );
    BOOST_REQUIRE( button );
    click( button, frame );
    waitFor( [&] { return clicks == 1; } );
    action( true, false );
    BOOST_CHECK( !gtk_widget_get_sensitive( button ) );
    click( button, frame );
    BOOST_CHECK_EQUAL( clicks, 1 );
    action( false, true );
    BOOST_CHECK( !gtk_widget_get_visible( button ) );
    action( true, true );
    waitFor( [&] { return gtk_widget_get_mapped( button ); } );
    click( button, frame );
    waitFor( [&] { return clicks == 2; } );
    frame->SetTitle( "Pending title during destruction" );
    frame->Destroy();
    wxTheApp->ProcessPendingEvents();
}

bool initialize() { return true; }
int main( int argc, char** argv )
{
    wxApp::SetInstance( new wxApp );
    if( !wxInitialize( argc, argv ) ) return 2;
    int result = boost::unit_test::unit_test_main( initialize, argc, argv );
    wxUninitialize();
    return result;
}

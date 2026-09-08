/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef AUTOMATION_UPDATE_CLIENT_H
#define AUTOMATION_UPDATE_CLIENT_H

#include <wx/event.h>
#include <wx/process.h>
#include <wx/stopwatch.h>
#include <wx/timer.h>
#include <nlohmann/json.hpp>
#include <functional>
#include <memory>
#include <string>

/** Owns only a local updater helper, never editor processes or documents.
 * All methods and callbacks run on the native event thread. */
class AUTOMATION_UPDATE_CLIENT : public wxEvtHandler
{
public:
    using OBSERVER = std::function<void( const nlohmann::json& )>;
    AUTOMATION_UPDATE_CLIENT( wxString aHelper, wxString aConfiguration, OBSERVER aObserver );
    ~AUTOMATION_UPDATE_CLIENT() override;

    void Start( int aCheckIntervalMs = 3600000 );
    void Check();
    void Cancel();
    bool IsRunning() const { return m_process != nullptr; }
    const nlohmann::json& Candidate() const { return m_candidate; }

private:
    void launch( bool aPrepare );
    void poll( wxTimerEvent& aEvent );
    void scheduledCheck( wxTimerEvent& aEvent );
    void finished( wxProcessEvent& aEvent );
    void drain();
    void readStream( wxInputStream* aStream, std::string& aBuffer );
    void notify( const nlohmann::json& aMessage );
    void fail( const std::string& aReason );

    wxString                   m_helper;
    wxString                   m_configuration;
    OBSERVER                   m_observer;
    wxTimer                    m_ioTimer;
    wxTimer                    m_checkTimer;
    wxStopWatch                m_elapsed;
    std::unique_ptr<wxProcess>  m_process;
    long                       m_pid = 0;
    long                       m_cancelAt = -1;
    bool                       m_prepare = false;
    bool                       m_invalid = false;
    std::string                m_stdout;
    std::string                m_stderr;
    nlohmann::json             m_terminal;
    nlohmann::json             m_candidate;
};

#endif

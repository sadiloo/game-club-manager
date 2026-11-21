Describe "Server wizard spec strings" {
    BeforeAll {
        $script:IncPath = (Resolve-Path (Join-Path $PSScriptRoot "..\server\ServerWizard.inc.iss")).Path
        $script:IssPath = (Resolve-Path (Join-Path $PSScriptRoot "..\server\CheckpointServer.iss")).Path
        $script:Inc = Get-Content -LiteralPath $script:IncPath -Raw
        $script:Iss = Get-Content -LiteralPath $script:IssPath -Raw
    }

    It "welcome WebView2 line when missing (§1.4)" {
        $script:Inc | Should -Match "WebView2 روی این رایانه نیست و همراه همین نصاب نصب می‌شود"
    }

    It "backup same-drive orange warning (§1.7)" {
        $script:Inc | Should -Match "این پوشه روی همان دیسک پشتیبان اصلی است"
    }

    It "install StatusMsg steps (§1.8)" {
        $script:Iss | Should -Match "در حال آماده کردن پایگاه داده"
        $script:Iss | Should -Match "در حال آماده کردن حساب‌ها"
        $script:Iss | Should -Match "در حال نصب سرویس‌های چک‌پوینت"
        $script:Iss | Should -Match "در حال تنظیم فایروال"
        $script:Iss | Should -Match "در حال تنظیم پشتیبان روزانه"
        $script:Iss | Should -Match "در حال روشن کردن سرور"
    }

    It "default-pin upgrade page title (§2.3)" {
        $script:Inc | Should -Match "عوض کردن رمز پیش‌فرض"
    }

    It "silent DEFAULTPINS switch (OQ-2)" {
        $script:Inc | Should -Match "DEFAULTPINS"
    }

    It "upgrade StatusMsg steps (§2.2)" {
        $script:Iss | Should -Match "در حال توقف سرور"
        $script:Iss | Should -Match "در حال گرفتن پشتیبان پیش از ارتقا"
        $script:Iss | Should -Match "upgrade-stop"
        $script:Iss | Should -Match "upgrade-start"
    }

    It "inline field errors not MsgBox (§1.5)" {
        $script:Inc | Should -Match "ShowQuery3Error"
        $script:Inc | Should -Match "clRed"
        $script:Inc | Should -Not -Match "MsgBox\(Err"
    }

    It "default-pin pages not capped at 5 (§2.3)" {
        $script:Inc | Should -Match "MAX_DEFAULT_PIN_PAGES"
        $script:Inc | Should -Not -Match "DefaultPinUserCount > 5"
    }

    It "open-sessions upgrade confirm (§2.2α)" {
        $script:Inc | Should -Match "Get-InstallerOpenSessionCount"
        $script:Inc | Should -Match "TaskDialogMsgBox"
        $script:Inc | Should -Match "جلسهٔ باز در جریان است"
    }

    It "default-pins many-user fixture (§2.3)" {
        $many = Join-Path $PSScriptRoot "..\ci\default-pins-silent-many.json"
        $items = Get-Content -LiteralPath $many -Raw | ConvertFrom-Json
        @($items).Count | Should -BeGreaterOrEqual 6
    }
}
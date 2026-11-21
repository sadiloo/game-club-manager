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
}
/* Fake powershell.exe for box wine runs of the Checkpoint installers (installer/tests/wine).
   - Logs each call's -InstallPhase (or "kiosk") to C:\fakeps-calls.log and prints one line to stdout.
   - Exits with the code configured in C:\fakeps-fail.txt ("<phase> <code>" per line) for that phase, else 0.
   - On a successful upgrade-backup it does what server-postinstall.ps1 does: writes a non-empty
     pre-upgrade dump and the marker C:\ProgramData\Checkpoint\logs\preupgrade-dump.current. */
#include <windows.h>
#include <stdio.h>
#include <string.h>
int main(void) {
  char *cl = GetCommandLineA(); char phase[128] = "none";
  char *p = strstr(cl, "-InstallPhase ");
  if (p) sscanf(p + 14, "%127s", phase); else if (strstr(cl, "kiosk-postinstall")) strcpy(phase, "kiosk");
  printf("FAKEPS phase=%s\n", phase); fflush(stdout);
  FILE *l = fopen("C:\\fakeps-calls.log", "a"); if (l) { fprintf(l, "%s\n", phase); fclose(l); }
  FILE *c = fopen("C:\\fakeps-cmdlines.log", "a"); if (c) { fprintf(c, "%s\n", cl); fclose(c); }
  int rc = 0; FILE *f = fopen("C:\\fakeps-fail.txt", "r"); char fp[128]; int code;
  if (f) { while (fscanf(f, "%127s %d", fp, &code) == 2) if (!strcmp(fp, phase)) { rc = code; break; } fclose(f); }
  if (rc == 0 && !strcmp(phase, "upgrade-backup")) {
    CreateDirectoryA("C:\\ProgramData", NULL); CreateDirectoryA("C:\\ProgramData\\Checkpoint", NULL);
    CreateDirectoryA("C:\\ProgramData\\Checkpoint\\backups", NULL); CreateDirectoryA("C:\\ProgramData\\Checkpoint\\logs", NULL);
    FILE *d = fopen("C:\\ProgramData\\Checkpoint\\backups\\pre-upgrade-wine-fake.dump", "w"); if (d) { fputs("PGDMP-fake", d); fclose(d); }
    FILE *m = fopen("C:\\ProgramData\\Checkpoint\\logs\\preupgrade-dump.current", "w");
    if (m) { fputs("C:\\ProgramData\\Checkpoint\\backups\\pre-upgrade-wine-fake.dump", m); fclose(m); }
  }
  return rc;
}

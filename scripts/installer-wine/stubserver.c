/* Stub GameClub.Server.exe for box wine runs: prints nothing; every execution is appended (with its
   command line) to C:\checkpoint-server-exec.log. CheckpointServer's service binPath points at this file,
   so any line without --check-default-pins means the (upgraded) server was started, i.e. it would
   have run its EF migrations. */
#include <windows.h>
#include <stdio.h>
int main(void) {
  FILE *l = fopen("C:\\checkpoint-server-exec.log", "a");
  if (l) { fprintf(l, "%s\n", GetCommandLineA()); fclose(l); }
  return 0;
}

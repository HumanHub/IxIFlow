#ifdef _WIN32
__declspec(dllimport) int fixture_helper(void);
#else
extern int fixture_helper(void);
#endif
int fixture_primary(void) { return fixture_helper(); }

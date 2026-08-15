#ifndef CCCOG_BAR_H
#define CCCOG_BAR_H

#include <stddef.h>

#define CCCOG_BAR_ABI_VERSION 1u

#ifdef __cplusplus
extern "C" {
#endif

/* Returns an owned UTF-8 JSON string. Release it with cccog_bar_free_string. */
char *cccog_bar_snapshot_json(const char *input_json);
/* Input: {"claudeCredentialPath":...,"grokAuthPath":...,"now":...}. */
char *cccog_bar_poll_quotas(const char *input_json);
void cccog_bar_free_string(char *value);

#ifdef __cplusplus
}
#endif

#endif /* CCCOG_BAR_H */

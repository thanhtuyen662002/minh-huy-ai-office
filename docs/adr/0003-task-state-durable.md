# ADR-0003: Durable Task State Outside LLM

Status: Accepted

Conversation/model context is never the sole state of work. Task/step/dependency/lease/checkpoint/result/approval state is stored durably and workers can resume after failure or model/session replacement.

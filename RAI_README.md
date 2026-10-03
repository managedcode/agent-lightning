# Responsible AI information

ManagedCode Agent Lightning for .NET is an embedded library for running agent rollouts, recording evaluation evidence, and optimizing authored prompt text. It uses application-supplied Microsoft.Extensions.AI or Microsoft Agent Framework clients and Microsoft.Extensions.AI.Evaluation. The package does not choose a model provider, connect to a provider, train model weights, or run a Python service.

## Intended use

The library is intended for developers who can configure and evaluate their own agents. Its released optimization capability is textual Automatic Prompt Optimization: it proposes prompt edits and compares them using caller-configured evaluation metrics and objectives. It does not fine-tune or otherwise update model weights.

Evaluation results depend on the caller's models, data, prompts, and metric configuration. Developers should use representative test data, inspect proposed prompt changes, and retain human review for consequential decisions. A measured score is not a guarantee of factual correctness, safety, fairness, or suitability for deployment.

## Limitations

The .NET port is not the upstream GPU/tensor-training stack. GPU reinforcement learning, VERL/vLLM execution, model-weight training, native checkpoint resume, and upstream provider-specific instrumentation are not implemented. The repository's parity matrix records current coverage and differences.

Do not use model outputs as the sole basis for high-impact decisions in legal, financial, healthcare, employment, or similarly consequential settings. Evaluate accuracy, privacy, security, bias, and applicable policies for the specific application before deployment.

## Data and privacy

The library operates on the data passed to it by the host application. The host selects the model client, evaluator, storage implementation, and telemetry configuration, and is responsible for their data handling, retention, access controls, and applicable legal requirements. Review the policies of any model or telemetry provider configured by the application.

## Provenance and license

This repository is a .NET port of Microsoft's Agent Lightning project. The upstream source project is [microsoft/agent-lightning](https://github.com/microsoft/agent-lightning); the compared v0.2 source revision is `4cd09ec900894cbcc0e832f1acfc3cbc1e2022b8`. The reference source is not a runtime dependency. This project is distributed under the [MIT License](./LICENSE).

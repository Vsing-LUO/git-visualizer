// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System;

namespace GitVisualizer.App.Dialogs;

public sealed record PushMonitorEntry(DateTimeOffset Timestamp, string Message);

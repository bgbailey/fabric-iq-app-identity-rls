interface RequestTicket {
  signal: AbortSignal;
  isCurrent: () => boolean;
}

export class RequestGate {
  private generation = 0;
  private controller: AbortController | null = null;

  invalidate(): void {
    this.generation += 1;
    this.controller?.abort();
    this.controller = null;
  }

  begin(): RequestTicket {
    this.invalidate();
    const generation = this.generation;
    const controller = new AbortController();
    this.controller = controller;
    return {
      signal: controller.signal,
      isCurrent: () => generation === this.generation && !controller.signal.aborted,
    };
  }
}


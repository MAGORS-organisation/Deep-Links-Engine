import { Component, type ErrorInfo, type ReactNode } from 'react';

interface Props {
  children: ReactNode;
  fallbackTitle: string;
  retryLabel: string;
}

interface State {
  error: Error | null;
}

/** Catches a render-time failure in one page so the shell and the other pages keep working. */
export class ErrorBoundary extends Component<Props, State> {
  override state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('Unhandled render error', error, info.componentStack);
  }

  private reset = () => this.setState({ error: null });

  override render(): ReactNode {
    if (this.state.error) {
      return (
        <div role="alert" className="callout callout-bad">
          <strong>{this.props.fallbackTitle}</strong>
          <code>{this.state.error.message}</code>
          <div>
            <button type="button" className="btn btn-sm" onClick={this.reset}>
              {this.props.retryLabel}
            </button>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}

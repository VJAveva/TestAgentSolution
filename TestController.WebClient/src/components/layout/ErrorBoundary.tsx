import { Component, type ReactNode } from 'react';
import { appLogger } from '../../lib/logger';

interface Props { children: ReactNode; }
interface State { error: Error | null; }

export default class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error) { return { error }; }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    appLogger.error('ErrorBoundary', error.message, { stack: error.stack, componentStack: info.componentStack });
    console.error('[ErrorBoundary]', error, info);
  }

  render() {
    if (this.state.error) {
      return (
        <div className="flex flex-col items-center justify-center h-screen bg-[#1E1E2E] text-[#CDD6F4] p-8">
          <h1 className="text-2xl font-bold text-[#F38BA8] mb-4">Application Error</h1>
          <pre className="max-w-2xl overflow-auto text-sm bg-[#313244] p-4 rounded-lg border border-[#585B70] whitespace-pre-wrap">
            {this.state.error.message}
            {'\n\n'}
            {this.state.error.stack}
          </pre>
          <button
            className="mt-6 px-4 py-2 bg-[#89B4FA] text-[#1E1E2E] font-medium rounded hover:bg-[#89B4FA]/80"
            onClick={() => window.location.reload()}
          >
            Reload Page
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}

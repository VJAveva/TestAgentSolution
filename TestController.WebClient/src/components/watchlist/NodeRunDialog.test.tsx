import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import NodeRunDialog from './NodeRunDialog';
import type { PipelineNodesResponse, TreeNode } from '../../types/api';

const runPipelineNode = vi.fn();
const fetchPipelineNodes = vi.fn();
const apiGet = vi.fn();

vi.mock('../../hooks/useExecution', () => ({
  useExecution: () => ({ runPipelineNode, fetchPipelineNodes }),
}));

vi.mock('../../lib/api', () => ({
  apiGet: (...args: unknown[]) => apiGet(...args),
}));

const actionNode: TreeNode = {
  id: 'n-9',
  nodeKind: 'Action',
  displayText: 'Copy Prepare-Agent.bat',
  tag: 'Copy Prepare-Agent.bat',
  executionStatus: 'Idle',
  children: [],
  isExpanded: false,
  depth: 4,
  nodePath: 'e0/c1/c1',
  watchItemTag: 'Revert 9 Nodes',
};

function nodesResponse(hasInitialize: boolean): PipelineNodesResponse {
  return {
    watchItemTag: 'Revert 9 Nodes',
    treeRevision: 'abc123def456',
    events: [{
      path: 'e0',
      kind: 'Event',
      name: 'Renamed',
      runnable: true,
      hasInitialize: false,
      agentName: '',
      children: [{
        path: 'e0/c1',
        kind: 'Group',
        name: 'Phase1',
        runnable: true,
        hasInitialize: false,
        agentName: '',
        children: [{
          path: 'e0/c1/c1',
          kind: 'Action',
          name: 'Copy Prepare-Agent.bat',
          runnable: true,
          hasInitialize,
          agentName: 'warmhist',
          children: [],
        }],
      }],
    }],
  };
}

describe('NodeRunDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiGet.mockResolvedValue({ parameters: { _BuildNumber: 'OAK_SP-2023-R2-SP2_20260915.5' } });
    runPipelineNode.mockResolvedValue({ sessionId: 's1' });
  });

  it('Should_HideInitializeOption_When_NodeInheritsNone', async () => {
    fetchPipelineNodes.mockResolvedValue(nodesResponse(false));

    render(<NodeRunDialog node={actionNode} onClose={vi.fn()} onRan={vi.fn()} />);

    expect(await screen.findByText('Only this action')).toBeTruthy();
    expect(screen.queryByText('This action + its Initialize')).toBeNull();
  });

  it('Should_OfferInitializeOption_When_NodeInheritsOne', async () => {
    fetchPipelineNodes.mockResolvedValue(nodesResponse(true));

    render(<NodeRunDialog node={actionNode} onClose={vi.fn()} onRan={vi.fn()} />);

    expect(await screen.findByText('This action + its Initialize')).toBeTruthy();
  });

  it('Should_ShowEffectiveBuildAndAgent_When_Loaded', async () => {
    fetchPipelineNodes.mockResolvedValue(nodesResponse(false));

    render(<NodeRunDialog node={actionNode} onClose={vi.fn()} onRan={vi.fn()} />);

    expect(await screen.findByText(/OAK_SP-2023-R2-SP2_20260915\.5/)).toBeTruthy();
    expect(screen.getByText(/agent warmhist/)).toBeTruthy();
  });

  it('Should_PostPathScopeAndRevision_When_Confirmed', async () => {
    fetchPipelineNodes.mockResolvedValue(nodesResponse(false));
    const onRan = vi.fn();

    render(<NodeRunDialog node={actionNode} onClose={vi.fn()} onRan={onRan} />);
    fireEvent.click(await screen.findByRole('button', { name: /Run single action/i }));

    await waitFor(() => expect(runPipelineNode).toHaveBeenCalledTimes(1));
    expect(runPipelineNode).toHaveBeenCalledWith('Revert 9 Nodes', {
      nodePath: 'e0/c1/c1',
      scope: 'OnlyThisNode',
      // Echoed back so the server can reject a run against a tree that hot-reloaded.
      treeRevision: 'abc123def456',
    });
    await waitFor(() => expect(onRan).toHaveBeenCalled());
  });

  it('Should_SendNodeWithInitialize_When_ThatScopeIsChosen', async () => {
    fetchPipelineNodes.mockResolvedValue(nodesResponse(true));

    render(<NodeRunDialog node={actionNode} onClose={vi.fn()} onRan={vi.fn()} />);
    fireEvent.click(await screen.findByRole('radio', { name: /This action \+ its Initialize/i }));
    fireEvent.click(screen.getByRole('button', { name: /Run single action/i }));

    await waitFor(() => expect(runPipelineNode).toHaveBeenCalledTimes(1));
    expect(runPipelineNode.mock.calls[0][1].scope).toBe('NodeWithInitialize');
  });

  it('Should_BlockRun_When_PathNoLongerResolves', async () => {
    // The tree reshuffled under the user: the locally-derived path is gone server-side.
    fetchPipelineNodes.mockResolvedValue({ ...nodesResponse(false), events: [] });

    render(<NodeRunDialog node={actionNode} onClose={vi.fn()} onRan={vi.fn()} />);

    expect(await screen.findByText(/no longer exists/i)).toBeTruthy();
    const run = screen.getByRole('button', { name: /Run single action/i }) as HTMLButtonElement;
    expect(run.disabled).toBe(true);

    fireEvent.click(run);
    expect(runPipelineNode).not.toHaveBeenCalled();
  });

  it('Should_NotCallOnRan_When_ServerRejectsTheRun', async () => {
    fetchPipelineNodes.mockResolvedValue(nodesResponse(false));
    runPipelineNode.mockRejectedValue({ status: 409, body: { message: 'Pipeline is locked' } });
    const onRan = vi.fn();

    render(<NodeRunDialog node={actionNode} onClose={vi.fn()} onRan={onRan} />);
    fireEvent.click(await screen.findByRole('button', { name: /Run single action/i }));

    expect(await screen.findByText('Pipeline is locked')).toBeTruthy();
    expect(onRan).not.toHaveBeenCalled();
  });
});

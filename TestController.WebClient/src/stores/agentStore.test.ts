import { describe, it, expect, beforeEach } from 'vitest';
import { useAgentStore } from './agentStore';
import type { AgentInfo } from '../types/api';

function makeAgent(name: string, status = 'Ready'): AgentInfo {
  return { name, address: `http://${name}:5200`, status };
}

describe('agentStore', () => {
  beforeEach(() => {
    useAgentStore.setState({ agents: [], selectedAgent: null });
  });

  // ?? applyHeartbeats (A1 batch update) ???????????????????????????????

  it('Should_UpdateManyAgents_When_ApplyHeartbeats', () => {
    useAgentStore.setState({
      agents: [makeAgent('Agent1', 'Offline'), makeAgent('Agent2', 'Offline'), makeAgent('Agent3', 'Offline')],
    });

    useAgentStore.getState().applyHeartbeats([
      { name: 'Agent1', status: 'Ready' },
      { name: 'Agent3', status: 'Running' },
    ]);

    const agents = useAgentStore.getState().agents;
    expect(agents.find(a => a.name === 'Agent1')!.status).toBe('Ready');
    expect(agents.find(a => a.name === 'Agent2')!.status).toBe('Offline'); // untouched
    expect(agents.find(a => a.name === 'Agent3')!.status).toBe('Running');
  });

  it('Should_MatchCaseInsensitively_When_ApplyHeartbeats', () => {
    useAgentStore.setState({ agents: [makeAgent('Agent1', 'Offline')] });
    useAgentStore.getState().applyHeartbeats([{ name: 'AGENT1', status: 'Ready' }]);
    expect(useAgentStore.getState().agents[0].status).toBe('Ready');
  });

  it('Should_IgnoreUnknownNames_When_ApplyHeartbeats', () => {
    useAgentStore.setState({ agents: [makeAgent('Agent1', 'Offline')] });
    useAgentStore.getState().applyHeartbeats([{ name: 'Ghost', status: 'Ready' }]);
    expect(useAgentStore.getState().agents).toHaveLength(1);
    expect(useAgentStore.getState().agents[0].status).toBe('Offline');
  });

  it('Should_StampLastChecked_When_ApplyHeartbeats', () => {
    useAgentStore.setState({ agents: [makeAgent('Agent1', 'Offline')] });
    useAgentStore.getState().applyHeartbeats([{ name: 'Agent1', status: 'Ready' }]);
    expect(useAgentStore.getState().agents[0].lastCheckedUtc).toBeTruthy();
  });

  it('Should_NoOp_When_ApplyHeartbeatsEmpty', () => {
    const seeded = [makeAgent('Agent1', 'Offline')];
    useAgentStore.setState({ agents: seeded });
    useAgentStore.getState().applyHeartbeats([]);
    expect(useAgentStore.getState().agents).toBe(seeded);
  });

  // ?? updateStatus (single) ???????????????????????????????????????????

  it('Should_UpdateOne_When_UpdateStatus', () => {
    useAgentStore.setState({ agents: [makeAgent('Agent1', 'Offline'), makeAgent('Agent2', 'Offline')] });
    useAgentStore.getState().updateStatus('agent2', 'Ready');
    const agents = useAgentStore.getState().agents;
    expect(agents.find(a => a.name === 'Agent1')!.status).toBe('Offline');
    expect(agents.find(a => a.name === 'Agent2')!.status).toBe('Ready');
  });

  // ?? add / remove / select ???????????????????????????????????????????

  it('Should_AddAgent_When_AddAgent', () => {
    useAgentStore.getState().addAgent(makeAgent('NewAgent'));
    expect(useAgentStore.getState().agents).toHaveLength(1);
  });

  it('Should_RemoveAndClearSelection_When_RemoveSelectedAgent', () => {
    useAgentStore.setState({ agents: [makeAgent('Agent1')], selectedAgent: 'Agent1' });
    useAgentStore.getState().removeAgent('AGENT1');
    expect(useAgentStore.getState().agents).toHaveLength(0);
    expect(useAgentStore.getState().selectedAgent).toBeNull();
  });
});
